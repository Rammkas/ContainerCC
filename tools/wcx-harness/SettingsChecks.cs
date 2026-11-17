using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace WcxHarness;

/// <summary>
/// Настройки пакера: ini, путь к которому TC подсказывает в PackSetDefaultParams (у TC это
/// pkplugin.ini рядом с программой), и диалог «Настроить» — экспорт ConfigurePacker.
///
/// Харнесс играет обе стороны. За TC: передаёт путь ini в tPackDefaultParamStruct, открывает диалог
/// тем же экспортом, который вызывает кнопка «Настроить», и работает с его окном через стандартные
/// сообщения (CB_GETCOUNT, CB_SETCURSEL, CB_SHOWDROPDOWN + WM_LBUTTONDOWN, BM_CLICK, BM_GETCHECK).
/// За пользователя: выбирает уровень списком, выбирает размер чанка кнопкой, в том числе кликает
/// мышью по раскрытому списку — именно на этом диалог раньше зависал.
///
/// Проверка результата — заголовок контейнера, прочитанный харнессом руками (36 байт ZST3):
/// именно туда упаковщик пишет чанк и уровень. И проверяется главное ограничение: настройки
/// действуют только на НОВЫЙ контейнер, а готовый контейнер сохраняет свои значения из заголовка.
///
/// Диалог открывается в отдельном потоке: ConfigurePacker не возвращает управление, пока окно
/// открыто (внутри него собственный цикл сообщений), поэтому харнесс ждёт окно из основного потока.
/// </summary>
internal static unsafe partial class Program
{
    // ── значения из ContainerWcx\PackerSettings.cs: с ними обязан совпадать и плагин, и харнесс
    private const string SettingsSection = "ContainerCC";
    private const string LevelKey = "CompressionLevel";
    private const string ChunkKey = "ChunkSizeKiB";

    private const int DefaultLevel = 3;
    private const int DefaultChunkKiB = 1024;
    private const int MinChunkKiB = 64;
    private const int MaxChunkKiB = 16384;
    private const int LevelCount = 22;

    // ── значения из ContainerWcx\OptionsDialog.cs: класс, заголовок и идентификаторы контролов
    private const string DialogClass = "ContainerCCOptions";
    private const string DialogCaption = "Настройки контейнера .cc";
    private const int IdLevelCombo = 1001;
    private const int IdNote = 1008; // вне id кнопок 1002..1007: два контрола с одним id — одна и та же проверка дважды
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int IdChunkNote = 1106; // строка «чанк из настроек вне списка кнопок»
    private const int IdChunkRangeLine = 1103; // строка «Любой другой размер чанка … в командной строке»
    private const int IdLevelLabel = 1101; // подписи тоже имеют id: без id GetDlgItem не находит контрол
    private const int IdChunkLabel = 1102;

    // Высота прежних подписей (шрифт версии 1.2.0 на 96 dpi): диалог обязан рисовать их не мельче.
    // Эталоном размера служит системный шрифт заголовка окна (NONCLIENTMETRICS.lfCaptionFont):
    // пользователь просил в диалоге тот же размер, что в заголовке, а не выбранный плагином.
    private const int PreviousFontHeightPx = 9;

    // Шесть радиокнопок чанка: id идут подряд (так Windows считает их одной группой), порядок
    // подписей обязан совпадать с PackerSettings.ChunkChoices.
    private const int IdChunkRadioFirst = 1002;
    private const int IdChunkRadioLast = 1007;
    private const int ChunkRadioCount = 6;

    private static readonly (int KiB, string Caption)[] ChunkChoices =
    [
        (64, "64 КиБ"),
        (256, "256 КиБ"),
        (1024, "1 МиБ"),
        (2048, "2 МиБ"),
        (8192, "8 МиБ"),
        (16384, "16 МиБ"),
    ];

    // ── заголовок контейнера ZST3 (Container_V3\Models.cs: ContainerHeader)
    private const int ContainerHeaderSize = 36;
    private const int ContainerVersion = 3;
    private const int HashLengthSha256 = 32;

    // ── сообщения окон: те же константы, что использует диалог плагина
    private const uint CbGetCount = 0x0146;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbSetCurSel = 0x014E;
    private const uint CbGetLbText = 0x0148;
    private const uint CbShowDropdown = 0x014F;
    private const uint CbGetItemHeight = 0x0154;
    private const uint CbGetDroppedState = 0x0157;
    private const uint BmGetcheck = 0x00F0;
    private const uint BmSetcheck = 0x00F1;
    private const uint BmClick = 0x00F5;
    private const uint LbItemFromPoint = 0x01A9;
    private const uint LbGetCursel = 0x0188;
    private const uint LbGetTopIndex = 0x018E;
    private const uint WmLbuttondown = 0x0201;
    private const uint WmLbuttonup = 0x0202;
    private const uint MkLbutton = 0x0001;
    private const uint WmGetfont = 0x0031;
    private const uint WmMousewheel = 0x020A;   // WM_MOUSEWHEEL: деление колёсика (величина — в старших 16 битах wParam)
    private const int WheelDelta = 120;         // WHEEL_DELTA: одно деление колёсика
    private const int GwOwner = 4;              // GW_OWNER: владельца окна (у раскрытого списка это сам комбосписок)
    private const int SpiGetNonClientMetrics = 0x0029; // SPI_GETNONCLIENTMETRICS: системные шрифты окон
    private const uint WmClose = 0x0010;
    private const uint WmQuit = 0x0012;
    private const uint WmCommand = 0x0111;
    private const uint BnClicked = 0;
    private const uint PmNoremove = 0;

    /// <summary>
    /// Класс окна, в котором Windows держит раскрытый список комбосписка. Он принадлежит потоку
    /// пакера, но дочерним для окна диалога не является: именно из-за таких окон цикл диалога
    /// должен брать сообщения всех окон потока.
    /// </summary>
    private const string ComboListClass = "ComboLBox";

    private const string NoValue = "\u0001нет значения\u0001";

    // ─────────────────────────── проход 14.1: настройки и диалог ───────────────────────────

    private static void RunPackerSettings(Plugin plugin, Options options, string settingsIni)
    {
        Section("проход 14.1: настройки пакера — ini из PackSetDefaultParams и диалог «Настроить»");

        if (!plugin.HasConfigure)
        {
            Console.WriteLine("  ПРОПУСК: экспорт ConfigurePacker не найден — диалога настроек у плагина нет, "
                + "значит уровень и размер чанка взять неоткуда");
            Console.WriteLine();
            return;
        }

        string settingsDir = Path.Combine(options.WorkDir, "settings");
        Recreate(settingsDir);

        string srcDir = Path.Combine(settingsDir, "src");
        Directory.CreateDirectory(srcDir);
        WriteText(Path.Combine(srcDir, "note.txt"), "Новый контейнер с выбранными настройками.\r\n");
        WritePattern(Path.Combine(srcDir, "data.bin"), 200 << 10);
        string[] names = new[] { "note.txt", "data.bin" };

        // Плагин при отказе писать в подсказанный TC файл уходит в %APPDATA%\ContainerCC\ContainerCC.ini.
        // В харнессе этот файл нам не нужен: проверяем, что плагин писал именно туда, куда показал TC,
        // и не создаёт ничего в профиле пользователя.
        string fallbackIni = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ContainerCC", "ContainerCC.ini");
        bool fallbackExistedBefore = File.Exists(fallbackIni);

        Console.WriteLine($"  ini пакеров, подсказанный плагину: {Quote(settingsIni)}");
        Console.WriteLine($"  файл плагина на случай отказа записи: {Quote(fallbackIni)} "
            + (fallbackExistedBefore ? "(в профиле он уже был)" : "(в профиле его нет)"));

        Check(!TryReadIniInt(settingsIni, LevelKey, out _), $"в подсказанном ini нет ключа {LevelKey}");
        Check(!TryReadIniInt(settingsIni, ChunkKey, out _), $"в подсказанном ini нет ключа {ChunkKey}");

        // ── 1. ключей нет: новый контейнер собирается значениями по умолчанию, как раньше
        string freshDefault = Path.Combine(settingsDir, "default.cc");
        Check(PackWithProgress(plugin, freshDefault, srcDir, names) == 0, "новый контейнер при пустом ini упакован");
        CheckHeader(freshDefault, DefaultChunkKiB * 1024, DefaultLevel, "пустой ini — значения по умолчанию");

        // ── 2. ini заполнен харнессом (так же его заполняет диалог): новый контейнер берёт эти значения
        Check(WriteIniInt(settingsIni, LevelKey, 19), $"записать {LevelKey}=19 в подсказанный ini");
        Check(WriteIniInt(settingsIni, ChunkKey, 256), $"записать {ChunkKey}=256 в подсказанный ini");
        Check(TryReadIniInt(settingsIni, LevelKey, out int readLevel) && readLevel == 19,
            $"{LevelKey} читается обратно как 19, получено {readLevel}");
        Check(TryReadIniInt(settingsIni, ChunkKey, out int readChunk) && readChunk == 256,
            $"{ChunkKey} читается обратно как 256, получено {readChunk}");

        string freshFromIni = Path.Combine(settingsDir, "from-ini.cc");
        Check(PackWithProgress(plugin, freshFromIni, srcDir, names) == 0, "новый контейнер при заполненном ini упакован");
        CheckHeader(freshFromIni, 256 * 1024, 19, "ini 19 / 256 КиБ — новый контейнер");

        Check(new FileInfo(freshFromIni).Length != new FileInfo(freshDefault).Length,
            "контейнеры с разными настройками получили разные файлы "
            + $"({new FileInfo(freshDefault).Length:N0} и {new FileInfo(freshFromIni).Length:N0} байт)");

        // ── 3. готовый контейнер: его чанк и уровень стоят в заголовке и при дописывании меняться не должны
        long beforeAppend = new FileInfo(freshDefault).Length;
        Check(PackWithProgress(plugin, freshDefault, srcDir, names) == 0,
            "дописывание в контейнер, созданный при других настройках, прошло");
        Check(new FileInfo(freshDefault).Length > beforeAppend,
            $"файл дописан: {beforeAppend:N0} → {new FileInfo(freshDefault).Length:N0} байт");
        CheckHeader(freshDefault, DefaultChunkKiB * 1024, DefaultLevel,
            "ini 19 / 256 КиБ, но контейнер создан раньше — заголовок не изменился");
        CheckListing(ReadListing(plugin, freshDefault, verbose: false), FreshModel(srcDir),
            "содержимое дописанного контейнера читается как раньше");

        // ── 4. плохой ini не должен срывать упаковку: значения вне диапазона заменяются default
        Check(WriteIniInt(settingsIni, LevelKey, 999), $"записать {LevelKey}=999 (вне 1..22)");
        Check(WriteIniInt(settingsIni, ChunkKey, 7), $"записать {ChunkKey}=7 (вне {MinChunkKiB}..{MaxChunkKiB})");

        string freshBadIni = Path.Combine(settingsDir, "bad-ini.cc");
        Check(PackWithProgress(plugin, freshBadIni, srcDir, names) == 0,
            "упаковка при бессмысленном ini не сорвалась");
        CheckHeader(freshBadIni, DefaultChunkKiB * 1024, DefaultLevel, "плохой ini — значения по умолчанию");

        // ── 5. возвращаем начальные значения: диалог обязан открыть диалог с текущим выбором
        Check(WriteIniInt(settingsIni, LevelKey, DefaultLevel), $"вернуть {LevelKey}={DefaultLevel}");
        Check(WriteIniInt(settingsIni, ChunkKey, DefaultChunkKiB), $"вернуть {ChunkKey}={DefaultChunkKiB}");

        // ── 6. диалог: посмотреть, кликнуть мышью по раскрытому списку, выбрать кнопкой, сорвать, закрыть крестиком
        CheckDialogRead(plugin);
        CheckComboDropdownMouse(plugin, settingsIni);
        CheckLevelComboWheel(plugin, settingsIni);
        CheckChunkOutsidePresets(plugin, settingsIni, settingsDir, srcDir, names);
        CheckDialogAccept(plugin, settingsIni, settingsDir, srcDir, names);
        CheckDialogCancel(plugin, settingsIni, IdCancel, "кнопка «Отмена»");
        CheckDialogCancel(plugin, settingsIni, 0, "закрытие окна (WM_CLOSE)");

        // Настройки из диалога работают так же, как записанные харнессом: проверка на новом контейнере.
        Check(TryReadIniInt(settingsIni, LevelKey, out int dialogLevel) && dialogLevel == 19,
            $"после диалога {LevelKey} = 19, получено {dialogLevel}");
        Check(TryReadIniInt(settingsIni, ChunkKey, out int dialogChunk) && dialogChunk == 256,
            $"после диалога {ChunkKey} = 256, получено {dialogChunk}");

        // ── 7. файл плагина в профиле создаваться не должен: пишем туда, что показал TC
        if (!fallbackExistedBefore && File.Exists(fallbackIni))
        {
            Fail($"плагин создал {Quote(fallbackIni)} в профиле пользователя, хотя подсказанный TC ini доступен для записи");
            File.Delete(fallbackIni);
        }
        else
        {
            Console.WriteLine("  файл настроек в профиле пользователя не появился: плагин пишет в подсказанный TC ini");
        }

        Console.WriteLine();
    }

    // ─────────────────────────── диалог «Настроить» ───────────────────────────

    /// <summary>
    /// Диалог открыт: комбосписок со всеми уровнями (пресеты — подписи к своим числам), текущий
    /// уровень выбран, размер чанка в поле — тот, что лежит в ini.
    /// </summary>
    private static void CheckDialogRead(Plugin plugin)
    {
        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        Check(WindowClass(session.Window) == DialogClass,
            $"класс окна диалога «{DialogClass}», получено «{WindowClass(session.Window)}»");
        Check(WindowText(session.Window) == DialogCaption,
            $"заголовок окна диалога «{DialogCaption}», получено «{WindowText(session.Window)}»");

        nint combo = GetDlgItem(session.Window, IdLevelCombo);
        Check(combo != 0, $"у диалога есть комбосписок уровня (GetDlgItem {IdLevelCombo})");
        Check(GetDlgItem(session.Window, IdChunkRadioFirst) != 0,
            $"у диалога есть кнопки размера чанка (GetDlgItem {IdChunkRadioFirst}..{IdChunkRadioLast})");

        int items = (int)SendMessageW(combo, CbGetCount, 0, 0);
        Check(items == LevelCount, $"в комбосписке {LevelCount} уровней (1..22), получено {items}");

        int selected = (int)SendMessageW(combo, CbGetCurSel, 0, 0);
        Check(selected == DefaultLevel - 1,
            $"диалог открывает текущий уровень {DefaultLevel} выбранным, выбран пункт {selected + 1}");

        if (items == LevelCount)
        {
            bool ordered = true;
            for (int index = 0; index < items; index++)
                ordered &= ComboItem(combo, index).StartsWith((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);

            Check(ordered, "пункт №N комбосписка — уровень N: список даёт и точный уровень, и пресет");

            Check(ComboItem(combo, 0).Contains("быстро"), "уровень 1 подписан «быстро»");
            Check(ComboItem(combo, 2).Contains("сбалансированно"), "уровень 3 подписан «сбалансированно»");
            Check(ComboItem(combo, 8).Contains("глубоко"), "уровень 9 подписан «глубоко»");
            Check(ComboItem(combo, 18).Contains("максимально"), "уровень 19 подписан «максимально»");
            Check(ComboItem(combo, 21) == "22", "уровень 22 — просто число, без подписи");
        }

        // Размер чанка — шесть кнопок, а не поле для ввода: их подписи, класс и отметка.
        nint[] radios = ChunkRadios(session.Window);
        Check(radios.All(radio => radio != 0),
            $"у диалога {ChunkRadioCount} кнопок размера чанка (id {IdChunkRadioFirst}..{IdChunkRadioLast}), " +
            $"найдено {radios.Count(radio => radio != 0)}");

        string radioClasses = string.Join(" | ", radios.Select(WindowClass));
        Check(radios.All(radio => radio != 0 && WindowClass(radio).Equals("Button", StringComparison.OrdinalIgnoreCase)),
            $"кнопки чанка — контролы класса Button (радиогруппа), а не поле, куда можно вписать что угодно; " +
            $"получено «{radioClasses}»");

        string gotCaptions = string.Join(" | ", radios.Select(WindowText));
        string wantCaptions = string.Join(" | ", ChunkChoices.Select(choice => choice.Caption));
        Check(gotCaptions == wantCaptions,
            $"подписи кнопок чанка по порядку: «{wantCaptions}», получено «{gotCaptions}»");

        Check(MarkedRadioIndex(radios) == IndexOfChunkKiB(DefaultChunkKiB),
            $"отмечена кнопка текущего чанка {DefaultChunkKiB} КиБ «{ChunkChoices[IndexOfChunkKiB(DefaultChunkKiB)].Caption}», " +
            $"получено «{MarkedRadioCaption(radios)}»");
        Check(MarkRadioCount(radios) == 1, "отмечена ровно одна кнопка чанка");

        // Пустая строка между кнопками чанка и строкой про командную строку (просьба пользователя).
        // Строки диалога стоят по сетке Y(row), поэтому расстояние между подписью «Размер чанка» и
        // кнопками — один шаг сетки, а между кнопками и строкой про командную строку — ровно два шага:
        // это и значит «одна пустая строка между ними». Проверка в долях, чтобы не зависеть от DPI.
        nint chunkLabel = GetDlgItem(session.Window, IdChunkLabel);
        nint rangeLine = GetDlgItem(session.Window, IdChunkRangeLine);
        Check(chunkLabel != 0 && rangeLine != 0,
            $"у диалога есть подпись чанка (id {IdChunkLabel}) и строка про командную строку " +
            $"(id {IdChunkRangeLine})");

        if (chunkLabel != 0 && rangeLine != 0 && radios[0] != 0)
        {
            NativeRect labelRect = new();
            NativeRect radioRect = new();
            NativeRect rangeRect = new();
            GetWindowRect(chunkLabel, ref labelRect);
            GetWindowRect(radios[0], ref radioRect);
            GetWindowRect(rangeLine, ref rangeRect);
            int labelToRadio = radioRect._top - labelRect._top;   // 4 - 3 = один шаг сетки
            int radioToRange = rangeRect._top - radioRect._top;   // 6 - 4 = два шага: кнопка, пустая строка, строка
            int freeGap = rangeRect._top - radioRect._bottom;     // сколько пустого места под кнопками
            int radioHeight = radioRect._bottom - radioRect._top;
            Check(labelToRadio > 0 && radioToRange >= 3 * labelToRadio / 2 && radioToRange <= 5 * labelToRadio / 2,
                $"между кнопками чанка и строкой про командную строку ровно одна пустая строка: " +
                $"шаг сетки {labelToRadio} px, от кнопок до строки {radioToRange} px (два шага), " +
                $"пустого места под кнопками {freeGap} px");
            Check(freeGap > 0 && freeGap >= labelToRadio / 2,
                $"под кнопками чанка осталось пустое место {freeGap} px — не меньше половины шага сетки " +
                $"({labelToRadio / 2} px при шаге {labelToRadio} px), кнопки и строка не слиплись; " +
                $"высота кнопки {radioHeight} px");
        }

        // Шрифт подписей. Диалог даёт один и тот же шрифт списку, кнопкам чанка, подписям и кнопкам
        // «OK»/«Отмена» (WM_GETFONT возвращает ровно тот HFONT, который контролу назначили).
        // Прежняя версия рисовала подписи шрифтом в 9 px на 96 dpi — проверка не даёт ему вернуться.
        nint font = ControlFont(combo);
        nint[] otherControls = [radios[0], radios[ChunkRadioCount - 1], GetDlgItem(session.Window, IdChunkLabel),
            GetDlgItem(session.Window, IdLevelLabel), GetDlgItem(session.Window, IdOk), GetDlgItem(session.Window, IdCancel)];
        Check(font != 0 && Array.TrueForAll(otherControls, control => control != 0 && ControlFont(control) == font),
            "весь диалог подписан одним шрифтом: список, кнопки чанка, подписи и «OK»/«Отмена»");

        int dpi = (int)GetDpiForWindow(session.Window);
        int fontHeight = FontHeightPx(font);
        int heightAt96 = dpi > 0 ? fontHeight * 96 / dpi : fontHeight;
        int captionAt96 = CaptionFontHeightAt96();

        Check(fontHeight > 0 && captionAt96 > 0
                && Math.Abs(heightAt96 - captionAt96) <= 1
                && heightAt96 > PreviousFontHeightPx,
            $"шрифт подписей {fontHeight} px (на 96 dpi это {heightAt96} px при DPI {dpi}) — тот же размер, " +
            $"что у системного шрифта заголовка окна ({captionAt96} px); прежние подписи были {PreviousFontHeightPx} px");

        Check(WindowText(GetDlgItem(session.Window, IdNote)).Length == 0, "подсказка об ошибке пустая, пока диалог не ругался");
        Check(WindowText(GetDlgItem(session.Window, IdChunkNote)).Length == 0,
            "предупреждения про чанк нет: значение из ini — одна из шести кнопок");
    }

    /// <summary>
    /// Регрессия на то, что видел пользователь: клик мышью по строке со списком уровней, после чего
    /// окно «подвисло и чего-то ждало», тогда как колёсико работало. Причина в том, что раскрытый
    /// список комбосписка Windows держит в отдельном окне «ComboLBox»: оно принадлежит потоку пакера,
    /// но дочерним для окна диалога не является. Цикл диалога, который берёт сообщения только своего
    /// окна и его детей, мышь этого списка не достаёт, и комбосписок, захватив мышь, ждёт их вечно.
    ///
    /// Проверяем не «окно не висит», а результат мышиного клика: раскрываем список, кликаем по
    /// конкретному пункту и требуем, чтобы выбрался именно он, чтобы список закрылся сам и чтобы
    /// диалог после этого продолжил отвечать.
    /// </summary>
    private static void CheckComboDropdownMouse(Plugin plugin, string settingsIni)
    {
        Check(TryReadIniInt(settingsIni, LevelKey, out int levelBefore), $"до клика по списку {LevelKey} прочитан");

        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        nint combo = GetDlgItem(session.Window, IdLevelCombo);

        // Отправная точка одна и та же во всех прогона: уровень 1, и клик обязан выбрать другой.
        // Иначе «выбор не изменился» легко принять за удачный клик по уже выбранному пункту.
        SendMessageW(combo, CbSetCurSel, 0, 0);
        SendMessageW(combo, CbShowDropdown, 1, 0);
        Check((int)SendMessageW(combo, CbGetDroppedState, 0, 0) == 1, "список уровней раскрыт (CB_SHOWDROPDOWN)");

        nint list = FindWindowExW(0, 0, ComboListClass, null);
        Check(list != 0,
            $"раскрытый список — окно класса «{ComboListClass}»: оно в потоке пакера, но не дочернее для диалога");

        if (list == 0)
        {
            PostMessageW(session.Window, WmClose, 0, 0);
            session.Join(8000);
            return;
        }

        int itemHeight = (int)SendMessageW(combo, CbGetItemHeight, 0, 0);
        Check(itemHeight > 0, $"строка списка уровней высотой {itemHeight} пикселей (CB_GETITEMHEIGHT)");

        // Координату клика берём из высоты строки, которую сообщает сам комбосписок, и сверяем её
        // с ответом списка (LB_ITEMFROMPOINT): если список сам считает, что в этой точке находится
        // нужный пункт, кликать по нему можно уверенно.
        const int wantItem = 6; // не первый и не последний пункт: «7» из 22
        int clickY = itemHeight * wantItem + itemHeight / 2;
        nint click = (nint)(long)(((long)clickY << 16) | 8);
        long hit = SendMessageW(list, LbItemFromPoint, 0, click).ToInt64();
        int fromPoint = unchecked((int)(hit & 0xFFFF));
        Check(fromPoint == wantItem && ((hit >> 16) & 0xFFFF) == 0,
            $"сам список считает, что в точке клика (8,{clickY}) лежит пункт {wantItem}: " +
            $"LB_ITEMFROMPOINT отвечает {fromPoint}{(((hit >> 16) & 0xFFFF) != 0 ? ", точка вне списка" : string.Empty)}");

        // Настоящий курсор ставим ровно на ту строку, по которой кликаем, и после клика возвращаем.
        // Комбосписок во время нажатия «захватывает» мышь, и настоящий ход мыши по другой строке
        // перетянул бы отметку туда: без этого клик зависел бы от того, где в эту секунду лежала рука
        // пользователя (в первом прогоне так и вышло — список сам показал пункт 6, а выбран был 1).
        GetCursorPos(out NativePoint cursorBefore);
        NativePoint clickOnScreen = new NativePoint { X = 8, Y = clickY };
        bool positioned = ClientToScreen(list, ref clickOnScreen) != 0
            && SetCursorPos(clickOnScreen.X, clickOnScreen.Y) != 0;
        Console.WriteLine($"  список уровней: строка {itemHeight} px, клик в (8,{clickY}), курсор был в " +
            $"({cursorBefore.X},{cursorBefore.Y}), наведение на строку " +
            $"{(positioned ? $"в ({clickOnScreen.X},{clickOnScreen.Y})" : "не удалось")}");

        int chosen = 0;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                Console.WriteLine($"  попытка {attempt - 1} выбрала пункт {chosen} — раскрываем список заново");
                PostMessageW(combo, CbShowDropdown, 1, 0);
                if (WaitDroppedState(combo, 1, 4000) != 1)
                    break;

                nint again = FindWindowExW(0, 0, ComboListClass, null);
                if (again != 0)
                    list = again; // при повторном раскрытии список может быть другим окном
            }

            // Нажатие и отпускание кнопки в окне списка: именно их должен достать из очереди цикл
            // диалога, потому что список — не дочернее окно диалога. Прежний цикл брал только свои
            // окна, и комбосписок, захвативший мышь, ждал эти сообщения вечно.
            if (positioned)
                SetCursorPos(clickOnScreen.X, clickOnScreen.Y); // курсор снова на нужной строке

            PostMessageW(list, WmLbuttondown, (nint)(long)MkLbutton, click);
            PostMessageW(list, WmLbuttonup, 0, click);
            chosen = WaitComboSel(combo, 0, 4000);

            if (chosen == wantItem)
                break;
        }

        if (positioned)
            SetCursorPos(cursorBefore.X, cursorBefore.Y);

        Console.WriteLine($"  после клика: CB_GETCURSEL → {chosen}, LB_GETCURSEL → " +
            $"{(int)SendMessageW(list, LbGetCursel, 0, 0)}, LB_GETTOPINDEX → {(int)SendMessageW(list, LbGetTopIndex, 0, 0)}");
        Check(chosen == wantItem,
            $"клик мышью по строке раскрытого списка выбрал ровно тот пункт, по которому кликали: " +
            $"пункт {wantItem} (уровень {wantItem + 1}), получено {chosen} (уровень {chosen + 1})");
        Check((int)SendMessageW(combo, CbGetDroppedState, 0, 0) == 0,
            "после клика список закрылся сам, и диалог не ждёт мышь");

        // Диалог обязан реагировать и после клика: «Отмена» закрывает его, настройки не тронуты.
        PressButton(session.Window, IdCancel);
        Check(session.Join(8000), "после клика мышью по раскрытому списку диалог закрывается кнопкой «Отмена»");
        Check(session.QuitPosted == false, "клик по раскрытому списку: WM_QUIT в очереди потока не появился");
        Check(TryReadIniInt(settingsIni, LevelKey, out int levelAfter) && levelAfter == levelBefore,
            $"выбор в списке сам по себе ничего не сохраняет: {LevelKey} остался {levelBefore}, получено {levelAfter}");
    }

    /// <summary>
    /// Колёсико мыши над списком уровней и клик по закрытому списку — то, о чём просил пользователь.
    /// Windows в закрытом комбосписке колёсико не обрабатывает вовсе, а в раскрытом только прокручивает
    /// список, и выбор остаётся прежним; поэтому диалог плагина перехватывает WM_MOUSEWHEEL и двигает
    /// выбранный уровень: один щелчок колеса = один уровень, вверх и вниз, и при закрытом, и при
    /// раскрытом списке. На краю списка колесо прокручивает сам список (там уровень менять некуда).
    /// Клик мышью по закрытому списку обязан его раскрыть — после этого уровень выбирают мышью.
    /// </summary>
    private static void CheckLevelComboWheel(Plugin plugin, string settingsIni)
    {
        Check(TryReadIniInt(settingsIni, LevelKey, out int levelBefore), $"до работы колёсиком {LevelKey} прочитан");

        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        nint combo = GetDlgItem(session.Window, IdLevelCombo);

        // ── закрытый список: колесо двигает уровень в обе стороны
        const int startItem = 4; // уровень 5: не первый и не последний, чтобы двигать в обе стороны
        SendMessageW(combo, CbSetCurSel, startItem, 0);
        Check((int)SendMessageW(combo, CbGetCurSel, 0, 0) == startItem,
            $"перед колесом выбран пункт {startItem} (уровень {startItem + 1})");

        PostWheel(combo, -WheelDelta); // колесо «от себя»: к более поздним уровням
        int moved = WaitComboSel(combo, startItem, 4000);
        Check(moved == startItem + 1,
            $"одно деление колеса «от себя» в закрытом списке передвинуло уровень на {moved - startItem:+0;-0;0}: " +
            $"пункт {moved} (уровень {moved + 1})");

        PostWheel(combo, WheelDelta); // колесо «на себя»: вспять
        int back = WaitComboSel(combo, startItem + 1, 4000);
        Check(back == startItem,
            $"одно деление колеса «на себя» вернуло уровень назад: пункт {back} (уровень {back + 1}) — ход вверх работает");

        PostWheel(combo, -3 * WheelDelta); // несколько делений одним движением (колесо со свободным ходом)
        int several = WaitComboSel(combo, startItem, 4000);
        Check(several == startItem + 3,
            $"три деления колеса одним движением передвинули уровень ровно на три: пункт {several} (уровень {several + 1})");

        // У края списка уровень двигать некуда: колесо не должно уводить выбор за границу списка.
        SendMessageW(combo, CbSetCurSel, 0, 0);
        PostWheel(combo, WheelDelta);
        Thread.Sleep(200);
        Check((int)SendMessageW(combo, CbGetCurSel, 0, 0) == 0,
            "у первого уровня колесо «на себя» ничего не ломает: выбор остался на первом пункте");

        int lastIndex = LevelCount - 1;
        SendMessageW(combo, CbSetCurSel, lastIndex, 0);
        PostWheel(combo, -WheelDelta);
        Thread.Sleep(200);
        Check((int)SendMessageW(combo, CbGetCurSel, 0, 0) == lastIndex,
            $"у последнего уровня колесо «от себя» ничего не ломает: выбор остался на пункте {lastIndex}");

        // ── раскрытый список: колесо двигает уровень, и список при этом остаётся открытым
        SendMessageW(combo, CbShowDropdown, 1, 0);
        Check(WaitDroppedState(combo, 1, 4000) == 1, "список уровней раскрыт (CB_SHOWDROPDOWN)");

        nint list = FindWindowExW(0, 0, ComboListClass, null);
        Check(list != 0, $"раскрытый список — окно класса «{ComboListClass}»");

        if (list != 0)
        {
            // Харнесс меряет, как Windows подвешивает окно раскрытого списка: его родитель — не
            // комбосписок, поэтому плагин опознаёт такой список по классу окна, а не по подчинённости.
            nint listParent = GetParent(list);
            Console.WriteLine($"  окно раскрытого списка {list}: класс «{WindowClass(list)}», GetParent → {listParent} " +
                $"(класс «{WindowClass(listParent)}», это комбосписок: {listParent == combo}, это диалог: {listParent == session.Window}), " +
                $"GetWindow(GW_OWNER) → {GetWindow(list, GwOwner)}");

            SendMessageW(combo, CbSetCurSel, startItem, 0);
            PostWheel(list, -2 * WheelDelta); // два деления, адресованные именно окну раскрытого списка
            int droppedMoved = WaitComboSel(combo, startItem, 4000);
            Check(droppedMoved == startItem + 2 && (int)SendMessageW(combo, CbGetDroppedState, 0, 0) == 1,
                $"два деления колеса над раскрытым списком передвинули уровень на два (пункт {droppedMoved}, " +
                $"уровень {droppedMoved + 1}) и список не закрылся: по нужному уровню можно кликнуть");

            PostWheel(list, WheelDelta);
            int droppedBack = WaitComboSel(combo, startItem + 2, 4000);
            Check(droppedBack == startItem + 1,
                $"колесо «на себя» в раскрытом списке тоже работает: пункт {droppedBack} (уровень {droppedBack + 1})");

            // Выбранный колесом уровень обязан остаться в поле зрения: список прокручивается за ним.
            SendMessageW(combo, CbSetCurSel, lastIndex, 0);
            Thread.Sleep(200);
            int top = (int)SendMessageW(list, LbGetTopIndex, 0, 0);
            Console.WriteLine($"  выбран последний уровень: LB_GETTOPINDEX → {top}, LB_GETCURSEL → " +
                $"{(int)SendMessageW(list, LbGetCursel, 0, 0)}");
            Check(top > 0, $"раскрытый список прокрутился к выбранному последнему уровню: верхний видимый пункт {top}");
        }

        // ── клик мышью по закрытому списку раскрывает его
        SendMessageW(combo, CbShowDropdown, 0, 0);
        WaitDroppedState(combo, 0, 4000);

        GetCursorPos(out NativePoint cursorBefore);
        var onCombo = new NativePoint { X = 12, Y = 8 }; // точка внутри закрытого комбосписка, у его стрелки
        bool parked = ClientToScreen(combo, ref onCombo) != 0 && SetCursorPos(onCombo.X, onCombo.Y) != 0;
        nint click = (nint)(long)(((long)8 << 16) | 12);

        PostMessageW(combo, WmLbuttondown, (nint)(long)MkLbutton, click);
        PostMessageW(combo, WmLbuttonup, 0, click);
        Check(WaitDroppedState(combo, 1, 4000) == 1,
            $"клик мышью по закрытому списку его раскрывает (курсор {(parked ? $"наведён в ({onCombo.X},{onCombo.Y})" : "не наведен")})");

        if (parked)
            SetCursorPos(cursorBefore.X, cursorBefore.Y);

        SendMessageW(combo, CbShowDropdown, 0, 0);
        WaitDroppedState(combo, 0, 4000);

        PressButton(session.Window, IdCancel);
        Check(session.Join(8000), "после колеса и клика диалог закрывается кнопкой «Отмена»");
        Check(session.QuitPosted == false, "колесо и клик по списку: WM_QUIT в очереди потока не появился");
        Check(TryReadIniInt(settingsIni, LevelKey, out int levelAfter) && levelAfter == levelBefore,
            $"ни колесо, ни клик сами по себе ничего не сохраняют: {LevelKey} остался {levelBefore}, получено {levelAfter}");
    }

    /// <summary>
    /// Сообщить окну о делении колёсика: в старших 16 битах wParam — величина (WHEEL_DELTA на деление,
    /// знак: «от себя» — минус), в lParam — экранные координаты курсора. Окно раскрытого списка
    /// комбосписка — не дочернее окно диалога: его сообщения должен доставать цикл, который берёт
    /// сообщения всех окон потока.
    /// </summary>
    private static void PostWheel(nint window, int delta)
    {
        nint wParam = (nint)(long)(((long)(unchecked((ushort)delta)) << 16));
        PostMessageW(window, WmMousewheel, wParam, 0);
    }

    /// <summary>
    /// В настройках лежит размер чанка, которого среди шести кнопок нет (его поставили командной
    /// строкой). Диалог не должен делать вид, что всё в порядке: он отмечает ближайшую кнопку и
    /// отдельной строкой говорит, что именно было в настройках. Пользователь выбирает свою кнопку —
    /// и этот выбор записывается и сразу работает при упаковке.
    /// </summary>
    private static void CheckChunkOutsidePresets(Plugin plugin, string settingsIni, string settingsDir, string srcDir, string[] names)
    {
        const int oddChunkKiB = 128; // этой кнопки нет, и она одинаково далеко от 64 КиБ и от 256 КиБ
        const int pickedChunkKiB = 2048;

        Check(WriteIniInt(settingsIni, ChunkKey, oddChunkKiB), $"записать {ChunkKey}={oddChunkKiB} (среди кнопок такого нет)");

        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        nint[] radios = ChunkRadios(session.Window);
        int nearest = IndexOfChunkKiB(MinChunkKiB);
        Check(MarkedRadioIndex(radios) == nearest,
            $"чанк {oddChunkKiB} КиБ: отмечена ближайшая кнопка «{ChunkChoices[nearest].Caption}», " +
            $"получено «{MarkedRadioCaption(radios)}»");

        string note = WindowText(GetDlgItem(session.Window, IdChunkNote));
        Check(note.Contains(oddChunkKiB.ToString(System.Globalization.CultureInfo.InvariantCulture)) && note.Length > 0,
            $"диалог предупреждает о чанке вне списка кнопок: «{note}»");

        int want = IndexOfChunkKiB(pickedChunkKiB);
        PostMessageW(radios[want], BmClick, 0, 0); // пользователь нажал «2 МиБ»

        int marked = WaitMarkedRadio(radios, nearest, 3000);
        Check(marked == want,
            $"нажатие кнопки «{ChunkChoices[want].Caption}» отметило её, а прежняя отметка снялась сама: " +
            $"получено «{MarkedRadioCaption(radios)}»");

        PressButton(session.Window, IdOk);
        Check(session.Join(8000), "ConfigurePacker вернулся после OK с выбранной кнопкой чанка");
        Check(TryReadIniInt(settingsIni, ChunkKey, out int savedChunk) && savedChunk == pickedChunkKiB,
            $"в {ChunkKey} записан выбранный чанк {pickedChunkKiB}, получено {savedChunk}");
        Check(TryReadIniInt(settingsIni, LevelKey, out int keptLevel) && keptLevel == DefaultLevel,
            $"уровень при этом не тронут: {DefaultLevel}, получено {keptLevel}");

        string fresh = Path.Combine(settingsDir, "chunk-2mib.cc");
        Check(PackWithProgress(plugin, fresh, srcDir, names) == 0, "новый контейнер после выбора кнопки упакован");
        CheckHeader(fresh, pickedChunkKiB * 1024, DefaultLevel,
            $"чанк {pickedChunkKiB} КиБ из кнопки диалога (прежний {oddChunkKiB} КиБ заменён осознанно)");
    }

    /// <summary>
    /// Диалог как его использует пользователь: сменить уровень списком, нажать другую кнопку чанка
    /// (отметка с прежней снимается сама собой), нажать OK — и получить ровно этот выбор в новых контейнерах.
    /// </summary>
    private static void CheckDialogAccept(Plugin plugin, string settingsIni, string settingsDir, string srcDir, string[] names)
    {
        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        nint combo = GetDlgItem(session.Window, IdLevelCombo);
        nint[] radios = ChunkRadios(session.Window);

        int before = MarkedRadioIndex(radios);
        Check(before == IndexOfChunkKiB(2048),
            $"диалог открыт с отмеченной кнопкой «2 МиБ» (так осталось после прошлой проверки), получено «{MarkedRadioCaption(radios)}»");

        // Защита TryAccept на случай, если ни одна кнопка не отмечена (например, радио-группа
        // рассоединилась). Пользователь такого сделать не может, поэтому отметку снимаем искусственно.
        // Если Windows снимать отметку auto-radio отказывается, проверку пропускаем: она про защиту, а не про диалог.
        SendMessageW(radios[before], BmSetcheck, 0, 0);

        if (MarkRadioCount(radios) != 0)
        {
            Console.WriteLine("  отметку с auto-radio снять не удалось — проверку «OK без выбранной кнопки» пропускаем");
        }
        else
        {
            PressButton(session.Window, IdOk);

            string badNote = WaitWindowText(GetDlgItem(session.Window, IdNote), string.Empty, 3000);
            Check(badNote.Length > 0, $"«OK» без отмеченной кнопки чанка объясняет причину: «{badNote}»");
            Check(IsWindow(session.Window) != 0, "диалог не закрылся, когда размер чанка не выбран");
            Check(TryReadIniInt(settingsIni, ChunkKey, out int untouchedChunk) && untouchedChunk == 2048,
                $"неудачный «OK» ничего не записал: {ChunkKey} остался 2048, получено {untouchedChunk}");
            session.MarkStillOpen();
        }

        SendMessageW(combo, CbSetCurSel, 18, 0); // уровень 19 («максимально»)
        Check((int)SendMessageW(combo, CbGetCurSel, 0, 0) == 18, "комбосписок принял выбор уровня 19");

        int want = IndexOfChunkKiB(256);
        PostMessageW(radios[want], BmClick, 0, 0);

        int marked = WaitMarkedRadio(radios, before, 3000);
        Check(marked == want, $"кнопка «{ChunkChoices[want].Caption}» отмечена, получено «{MarkedRadioCaption(radios)}»");
        Check(MarkRadioCount(radios) == 1, "отмечена ровно одна кнопка чанка: двух выбранных быть не может");

        PressButton(session.Window, IdOk);

        Check(session.Join(8000), "ConfigurePacker вернулся после OK — диалог закрыт");
        Check(IsWindow(session.Window) == 0, "окно диалога уничтожено");
        Check(session.QuitPosted == false,
            "диалог не послал WM_QUIT: цикл сообщений того потока, что открыл его, остаётся рабочим (иначе TC бы закрылся)");

        CheckHeaderAfterDialog(plugin, settingsIni, settingsDir, srcDir, names);
    }

    /// <summary>Настройки, сохранённые диалогом, работают ровно так же, как записанные в ini руками.</summary>
    private static void CheckHeaderAfterDialog(Plugin plugin, string settingsIni, string settingsDir, string srcDir, string[] names)
    {
        string fresh = Path.Combine(settingsDir, "from-dialog.cc");
        Check(PackWithProgress(plugin, fresh, srcDir, names) == 0, "новый контейнер после диалога упакован");
        CheckHeader(fresh, 256 * 1024, 19, "выбор из диалога «Настроить» (уровень 19, чанк 256 КиБ)");

        CheckListing(ReadListing(plugin, fresh, verbose: false), FreshModel(srcDir),
            "контейнер с настройками из диалога читается и распаковывается обычным циклом");
        Check(ExtractAllW(plugin, fresh, Path.Combine(settingsDir, "out-dialog")) == 0,
            "контейнер с настройками из диалога извлечён без ошибок");
    }

    /// <summary>
    /// Настройки не сохранились: пользователь нажал «Отмена» или закрыл окно. Значения в ini обязаны
    /// остаться такими, какими были до диалога.
    /// </summary>
    private static void CheckDialogCancel(Plugin plugin, string settingsIni, int buttonId, string what)
    {
        Check(TryReadIniInt(settingsIni, LevelKey, out int levelBefore), $"перед отменой {LevelKey} прочитан");
        Check(TryReadIniInt(settingsIni, ChunkKey, out int chunkBefore), $"перед отменой {ChunkKey} прочитан");

        using SettingsDialog session = SettingsDialog.Open(plugin);
        if (!session.IsOpen)
            return;

        nint combo = GetDlgItem(session.Window, IdLevelCombo);
        nint[] radios = ChunkRadios(session.Window);
        int initialMark = MarkedRadioIndex(radios);

        SendMessageW(combo, CbSetCurSel, 0, 0);
        int selectedLevel = (int)SendMessageW(combo, CbGetCurSel, 0, 0);
        Check(selectedLevel == 0, $"перед отменой в списке уровень 1 (получено {selectedLevel + 1})");

        int wantRadio = IndexOfChunkKiB(MinChunkKiB);
        PostMessageW(radios[wantRadio], BmClick, 0, 0);

        // Ждём, а не проверяем сразу: BM_CLICK — PostMessage, и поток диалога мог ещё не успеть
        // переставить отметку. Прежняя отметка снялась сама — этого и ждём.
        int marked = WaitMarkedRadio(radios, initialMark, 3000);
        Check(marked == wantRadio && MarkRadioCount(radios) == 1,
            $"перед отменой пользователь успел отметить кнопку «{ChunkChoices[wantRadio].Caption}» " +
            $"и уровень 1: отмечено «{MarkedRadioCaption(radios)}»");

        if (buttonId == 0)
            PostMessageW(session.Window, WmClose, 0, 0);
        else
            PressButton(session.Window, buttonId);

        Check(session.Join(8000), $"ConfigurePacker вернулся после {what}");
        Check(IsWindow(session.Window) == 0, $"окно диалога уничтожено после {what}");
        Check(session.QuitPosted == false, $"{what}: WM_QUIT в очереди потока не появился");

        Check(TryReadIniInt(settingsIni, LevelKey, out int levelAfter) && levelAfter == levelBefore,
            $"{what}: {LevelKey} не изменился ({levelBefore} → {levelAfter})");
        Check(TryReadIniInt(settingsIni, ChunkKey, out int chunkAfter) && chunkAfter == chunkBefore,
            $"{what}: {ChunkKey} не изменился ({chunkBefore} → {chunkAfter})");
    }

    // ─────────────────────────── потоки и окно ───────────────────────────

    /// <summary>
    /// Один вызов ConfigurePacker: он не возвращает управление, пока открыто окно, поэтому живёт
    /// в отдельном потоке. Основной поток находит окно и говорит с ним сообщениями.
    /// </summary>
    private sealed unsafe class SettingsDialog : IDisposable
    {
        private readonly Plugin _plugin;
        private readonly Thread _thread;

        public nint Window { get; private set; }
        public bool IsOpen { get; private set; }
        public bool QuitPosted { get; private set; }

        private SettingsDialog(Plugin plugin)
        {
            _plugin = plugin;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public static SettingsDialog Open(Plugin plugin)
        {
            SettingsDialog session = new(plugin);
            session.Window = session.WaitForWindow();
            session.IsOpen = session.Window != 0;

            if (!session.IsOpen)
                Fail($"диалог «{DialogCaption}» (класс {DialogClass}) не появился в течение 5 секунд после ConfigurePacker");

            return session;
        }

        public bool Join(int timeoutMs)
        {
            bool joined = _thread.Join(timeoutMs);
            if (!joined)
            {
                Fail("ConfigurePacker не вернулся: окно диалога закрываем принудительно");
                if (Window != 0)
                    PostMessageW(Window, WmClose, 0, 0);
                joined = _thread.Join(5000);
            }

            return joined;
        }

        /// <summary>Диалог ещё открыт: следующий шаг проверяем после этого, а не после Join.</summary>
        public void MarkStillOpen()
        {
            if (_thread.Join(2000))
                Fail("ConfigurePacker вернулся, хотя диалог должен был остаться открытым (ошибочное значение)");
        }

        public void Dispose()
        {
            if (IsOpen && IsWindow(Window) != 0)
            {
                PostMessageW(Window, WmClose, 0, 0);
                Join(8000);
            }

            GC.SuppressFinalize(this);
        }

        private void Run()
        {
            // Точно тот вызов, который делает TC по кнопке «Настроить»: родитель — окно TC,
            // здесь родитель не нужен (0), диалог тогда центрируется по экрану.
            _plugin.ConfigurePacker(IntPtr.Zero, _plugin.Module);

            // PostQuitMessage из диалога убил бы главный цикл сообщений потока: для TC это закрытие
            // программы. Проверяем очередь потока сразу после возврата ConfigurePacker.
            if (PeekMessageW(out NativeMessage message, IntPtr.Zero, 0, 0, PmNoremove) != 0)
            {
                if (message._message == WmQuit)
                    QuitPosted = true;
                else
                    Console.WriteLine($"  заметка: в очереди потока после ConfigurePacker осталось сообщение 0x{message._message:X4}");
            }
        }

        private nint WaitForWindow()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                nint byClass = FindWindowW(DialogClass, null);
                if (byClass != 0)
                    return byClass;

                // Если имя класса не опознано, окно всё равно найдём по заголовку: следующая проверка
                // покажет, какое класс имя у окна на самом деле.
                nint byCaption = FindWindowW(null, DialogCaption);
                if (byCaption != 0)
                    return byCaption;

                if (!_thread.IsAlive)
                    return 0;

                Thread.Sleep(50);
            }

            return 0;
        }
    }

    // ─────────────────────────── контейнер: заголовок своими руками ───────────────────────────

    private static int PackWithProgress(Plugin plugin, string container, string srcDir, string[] names)
    {
        using Progress progress = new();
        plugin.SetProcessDataProcW(0, progress.Handle);
        return PackW(plugin, container, null, srcDir, names, 0);
    }

    private static ContainerModel FreshModel(string srcDir)
    {
        ContainerModel model = new();
        model.MergeFrom(Snapshot.Collect(srcDir));
        return model;
    }

    /// <summary>
    /// Читает 36 байт заголовка ZST3: magic, версия, размер чанка, длина хэша, уровень сжатия.
    /// Харнесс делает это сам, потому что проверить настройки можно только по тому, что упаковщик
    /// реально записал в файл.
    /// </summary>
    private static (int Version, int ChunkSize, int HashLength, int Level)? ReadContainerHeader(string path)
    {
        if (!File.Exists(path))
            return null;

        Span<byte> header = stackalloc byte[ContainerHeaderSize];

        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Read(header) != ContainerHeaderSize)
                return null;
        }

        if (header[0] != (byte)'Z' || header[1] != (byte)'S' || header[2] != (byte)'T' || header[3] != (byte)'3')
            return null;

        return (
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4)));
    }

    private static void CheckHeader(string container, int expectChunkBytes, int expectLevel, string what)
    {
        (int Version, int ChunkSize, int HashLength, int Level)? header = ReadContainerHeader(container);
        if (header is null)
        {
            Fail($"{what}: заголовок {Quote(Path.GetFileName(container))} не читается как 36-байтный ZST3");
            return;
        }

        Check(header.Value.Version == ContainerVersion,
            $"{what}: версия контейнера {ContainerVersion}, получено {header.Value.Version}");
        Check(header.Value.HashLength == HashLengthSha256,
            $"{what}: длина хэша {HashLengthSha256} (SHA-256, хэш в настройках не менялся), получено {header.Value.HashLength}");
        Check(header.Value.ChunkSize == expectChunkBytes,
            $"{what}: размер чанка {expectChunkBytes:N0} байт, получено {header.Value.ChunkSize:N0}");
        Check(header.Value.Level == expectLevel,
            $"{what}: уровень сжатия {expectLevel}, получено {header.Value.Level}");
    }

    // ─────────────────────────── ini ───────────────────────────

    private static bool TryReadIniInt(string path, string key, out int value)
    {
        value = 0;
        if (!File.Exists(path))
            return false;

        string section = SettingsSection;
        string noValue = NoValue;
        Span<char> buffer = stackalloc char[32];

        fixed (char* sectionPtr = section)
        fixed (char* keyPtr = key)
        fixed (char* defaultPtr = noValue)
        fixed (char* bufferPtr = buffer)
        {
            uint copied = GetPrivateProfileStringW(sectionPtr, keyPtr, defaultPtr, bufferPtr, buffer.Length, path);
            string text = new string(bufferPtr, 0, (int)copied);

            if (text.Length == 0 || text == noValue)
                return false;

            return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    private static bool WriteIniInt(string path, string key, int number)
    {
        string section = SettingsSection;
        string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        fixed (char* sectionPtr = section)
        fixed (char* keyPtr = key)
        fixed (char* valuePtr = text)
            return WritePrivateProfileStringW(sectionPtr, keyPtr, valuePtr, path) != 0;
    }

    // ─────────────────────────── сообщения контролов ───────────────────────────

    private static string ComboItem(nint combo, int index)
    {
        Span<char> buffer = stackalloc char[128];
        fixed (char* bufferPtr = buffer)
        {
            SendMessageW(combo, CbGetLbText, index, (nint)bufferPtr);
            return new string(bufferPtr);
        }
    }

    /// <summary>Шрифт контрола: диалог плагина назначает всем своим контролам один HFONT через WM_SETFONT.</summary>
    private static nint ControlFont(nint control)
        => control == 0 ? 0 : SendMessageW(control, WmGetfont, 0, 0);

    /// <summary>
    /// Высота знака шрифта в пикселях: GetObjectW описывает HFONT структурой LOGFONTW, первое поле
    /// которой lfHeight. Диалог задаёт шрифт отрицательной высотой — это высота знака, а не строки.
    /// </summary>
    private static int FontHeightPx(nint font)
    {
        if (font == 0)
            return 0;

        Span<byte> logFont = stackalloc byte[96]; // LOGFONTW занимает 92 байта
        fixed (byte* buffer = logFont)
        {
            if (GetObjectW(font, logFont.Length, buffer) == 0)
                return 0;

            return -BitConverter.ToInt32(logFont);
        }
    }

    /// <summary>
    /// Высота системного шрифта заголовка окна (NONCLIENTMETRICS.lfCaptionFont) в пересчёте на 96 dpi:
    /// именно этим шрифтом Windows рисует строку заголовка любого окна, и диалог «Настроить» обязан
    /// брать размер отсюда, а не выдумывать его сам. SystemParametersInfoW отдаёт метрики для плотности
    /// главного экрана, поэтому высоту делим на DPI рабочего стола и умножаем на 96.
    /// </summary>
    private static int CaptionFontHeightAt96()
    {
        NativeNonClientMetrics metrics = new NativeNonClientMetrics();
        metrics.cbSize = (uint)Unsafe.SizeOf<NativeNonClientMetrics>();

        if (SystemParametersInfoW(SpiGetNonClientMetrics, 0, ref metrics, 0) == 0)
            return 0;

        int captionHeight = Math.Abs(metrics.lfCaptionFont.lfHeight);
        int systemDpi = (int)GetDpiForWindow(GetDesktopWindow());

        if (captionHeight <= 0)
            return 0;

        return systemDpi > 0 ? captionHeight * 96 / systemDpi : captionHeight;
    }

    // ─────────────────────────── кнопки размера чанка ───────────────────────────

    /// <summary>Кнопки чанка по их id: отсутствующая кнопка — ноль в списке, чтобы проверка показала пропажу, а не упала.</summary>
    private static nint[] ChunkRadios(nint dialog)
    {
        var radios = new nint[ChunkRadioCount];
        for (int index = 0; index < ChunkRadioCount; index++)
            radios[index] = GetDlgItem(dialog, IdChunkRadioFirst + index);

        return radios;
    }

    private static int IndexOfChunkKiB(int chunkKiB)
    {
        for (int index = 0; index < ChunkChoices.Length; index++)
            if (ChunkChoices[index].KiB == chunkKiB)
                return index;

        return -1;
    }

    private static bool RadioChecked(nint radio)
        => radio != 0 && SendMessageW(radio, BmGetcheck, 0, 0) != 0;

    private static int MarkRadioCount(nint[] radios) => radios.Count(RadioChecked);

    /// <summary>Индекс отмеченной кнопки; -1 — если отмечено не ровно одна (иначе проверка была бы слепой).</summary>
    private static int MarkedRadioIndex(nint[] radios)
    {
        if (MarkRadioCount(radios) != 1)
            return -1;

        for (int index = 0; index < radios.Length; index++)
            if (RadioChecked(radios[index]))
                return index;

        return -1;
    }

    private static string MarkedRadioCaption(nint[] radios)
    {
        int index = MarkedRadioIndex(radios);
        return index >= 0 ? WindowText(radios[index]) : "ни одна кнопка не отмечена";
    }

    /// <summary>Ждём, пока отмеченной окажется другая кнопка: отметка с прежней должна сняться сама.</summary>
    private static int WaitMarkedRadio(nint[] radios, int differentFrom, int timeoutMs)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 50)
        {
            int index = MarkedRadioIndex(radios);
            if (index >= 0 && index != differentFrom)
                return index;

            Thread.Sleep(50);
        }

        return MarkedRadioIndex(radios);
    }

    /// <summary>Ждёт, пока комбосписок не окажется раскрытым (или закрытым) — он делает это сам, в своём потоке.</summary>
    private static int WaitDroppedState(nint combo, int want, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            if ((int)SendMessageW(combo, CbGetDroppedState, 0, 0) == want)
                return want;

            Thread.Sleep(50);
        }

        return (int)SendMessageW(combo, CbGetDroppedState, 0, 0);
    }

    private static int WaitComboSel(nint combo, int differentFrom, int timeoutMs)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 50)
        {
            int selected = (int)SendMessageW(combo, CbGetCurSel, 0, 0);
            if (selected != differentFrom)
                return selected;

            Thread.Sleep(50);
        }

        return (int)SendMessageW(combo, CbGetCurSel, 0, 0);
    }

    private static string WindowText(nint window)
    {
        if (window == 0)
            return string.Empty;

        Span<char> buffer = stackalloc char[256];
        fixed (char* bufferPtr = buffer)
        {
            int length = GetWindowTextW(window, bufferPtr, buffer.Length);
            return length <= 0 ? string.Empty : new string(bufferPtr, 0, length);
        }
    }

    private static string WindowClass(nint window)
    {
        Span<char> buffer = stackalloc char[64];
        fixed (char* bufferPtr = buffer)
        {
            int length = GetClassNameW(window, bufferPtr, buffer.Length);
            return length <= 0 ? string.Empty : new string(bufferPtr, 0, length);
        }
    }

    /// <summary>
    /// Нажатие кнопки так, как его делает сам Windows: родитель диалога получает WM_COMMAND
    /// с идентификатором кнопки и BN_CLICKED. Именно PostMessage: кнопка живёт в потоке плагина,
    /// и SendMessage к ней означал бы «ждать ответ от потока, который сейчас занят моим сообщением».
    /// </summary>
    private static void PressButton(nint dialog, int id)
        => PostMessageW(dialog, WmCommand, (nint)((long)BnClicked << 16 | (uint)id), 0);

    private static string WaitWindowText(nint window, string differentFrom, int timeoutMs)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 50)
        {
            string text = WindowText(window);
            if (text != differentFrom)
                return text;

            Thread.Sleep(50);
        }

        return WindowText(window);
    }

    // ─────────────────────────── win32 ───────────────────────────

    private struct NativeMessage
    {
        public nint _hwnd;
        public uint _message;
        public nint _wParam;
        public nint _lParam;
        public uint _time;
        public int _ptX;
        public int _ptY;
    }

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>RECT из windef.h — им харнесс меряет строки диалога (пустая строка между ними).</summary>
    private struct NativeRect
    {
        public int _left;
        public int _top;
        public int _right;
        public int _bottom;
    }

    /// <summary>LOGFONTW (92 байта): харнесс сверяет размер шрифта диалога с системным шрифтом заголовка.</summary>
    private struct NativeLogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        public fixed char lfFaceName[32]; // LF_FACESIZE
    }

    /// <summary>NONCLIENTMETRICSW (504 байта) из winuser.h — порядок полей как в заголовке SDK.</summary>
    private struct NativeNonClientMetrics
    {
        public uint cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;
        public NativeLogFont lfCaptionFont;
        public int iSmCaptionWidth;
        public int iSmCaptionHeight;
        public NativeLogFont lfSmCaptionFont;
        public int iMenuWidth;
        public int iMenuHeight;
        public NativeLogFont lfMenuFont;
        public NativeLogFont lfStatusFont;
        public NativeLogFont lfMessageFont;
        public int iPaddedBorderWidth;
    }

    /// <summary>Размер системного шрифта заголовка: эталон, с которым харнесс сравнивает шрифт диалога.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SystemParametersInfoW(int uiAction, int uiParam, ref NativeNonClientMetrics param, int flags);

    /// <summary>Рабочий стол: по его окну узнают DPI главного экрана — на него и отдаёт метрики SystemParametersInfoW.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDesktopWindow();

    /// <summary>Родитель или владелец окна: у окна раскрытого списка комбосписка это сам комбосписок.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    /// <summary>Экранные координаты окна: ими харнесс меряет строки диалога и пустую строку между ними.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowRect(nint window, ref NativeRect rect);

    /// <summary>Курсор мыши: его положение важно при клике по раскрытому списку (см. CheckComboDropdownMouse).</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetCursorPos(int x, int y);

    /// <summary>Координаты клиента окна — в экранные: чтобы навести курсор ровно на нужную строку списка.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string? className, string? windowName);

    /// <summary>Поиск окна по классу среди верхних окон: так находят окно раскрытого списка комбосписка.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint afterChild, string? className, string? windowName);

    /// <summary>Описание объекта GDI (здесь — шрифта) в его родной структуре.</summary>
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetObjectW(nint handle, int size, byte* buffer);

    /// <summary>DPI окна: высоты шрифтов сравнивают только приведёнными к одной плотности (96 dpi).</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDlgItem(nint dialog, int id);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int IsWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern unsafe int GetWindowTextW(nint window, char* text, int maxChars);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern unsafe int GetClassNameW(nint window, char* className, int maxChars);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int PeekMessageW(out NativeMessage message, nint window, uint filterMin, uint filterMax, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe uint GetPrivateProfileStringW(
        char* section, char* key, char* defaultValue, char* buffer, int bufferSize, string filePath);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe int WritePrivateProfileStringW(char* section, char* key, char* value, string filePath);
}
