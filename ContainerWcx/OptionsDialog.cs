using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ContainerWcx;

/// <summary>
/// Диалог «Настроить» в окне упаковывания Total Commander: TC вызывает экспорт ConfigurePacker,
/// когда пользователь нажимает кнопку «Настроить» в окне «Упаковать файлы» (Alt+F5).
///
/// NativeAOT не даёт ни WinForms, ни WPF, поэтому окно собрано вручную на user32: свой класс окна,
/// дочерние controls (STATIC, COMBOBOX, BUTTON), своё оконное сообщение и цикл GetMessageW.
///
/// Цикл берёт сообщения всех окон потока (hWnd = NULL), а не только своего окна и его детей:
/// раскрытый список комбосписка Windows держит в отдельном окне «ComboLBox», которое принадлежит
/// потоку Total Commander, но дочерним для нашего окна не является. Фильтр по нашему окну такие
/// сообщения из очереди никогда не забирает, и комбосписок, захватив мышь, ждал их вечно — окно
/// выглядело зависшим. Подробности в цикле сообщений ниже.
///
/// PostQuitMessage здесь вызывать нельзя: плагин живёт в процессе Total Commander, и WM_QUIT
/// оборвал бы цикл сообщений самого TC. Поэтому цикл выходит по нашему флагу _closed, а не по
/// WM_QUIT. Но чужой WM_QUIT, который всё-таки придёт, цикл честно возвращает в очередь, чтобы
/// цикл TC завершился как обычно, а не потерял запрос на закрытие.
///
/// В диалоге только уровень сжатия (список) и размер чанка (шесть кнопок с фиксированными
/// значениями), и действуют они только на новые контейнеры (подробности в PackerSettings). Метод
/// хэша не выбирается: у заголовка контейнера нет для него поля, а контейнер с другой длиной хэша
/// старая версия плагина уже не открыла бы.
/// </summary>
internal static unsafe class OptionsDialog
{
    /// <summary>Имя класса окна и заголовок: их читает харнесс (FindWindowW), чтобы проверить диалог.</summary>
    public const string ClassName = "ContainerCCOptions";
    public const string Caption = "Настройки контейнера .cc";

    public const int IdLevelCombo = 1001;
    public const int IdNote = 1008;  // строка сообщения/ошибки: её проверяет харнесс

    // Размер чанка — шесть радиокнопок с фиксированными значениями (PackerSettings.ChunkChoices).
    // Идентификаторы идут подряд: Windows сам считает соседние id одной группой, и тогда клик по
    // одной кнопке сам снимает отметку с остальных. Харнесс находит кнопки по этим id.
    public const int IdChunkRadioFirst = 1002;
    public const int IdChunkRadioLast = 1007; // IdChunkRadioFirst + 5, по числу ChunkChoices

    // Диапазон 1002..1007 занят кнопками: id у контролов должен быть единственным, иначе
    // GetDlgItem вернёт одним и тем же окном и заметку, и кнопку (проверка стала бы слепой).

    public const int IdOk = 1;        // IDOK
    public const int IdCancel = 2;    // IDCANCEL

    // Подписи тоже с идентификаторами: у дочернего окна контрола без id нет, и найти его
    // (GetDlgItem) нельзя. Харнесс по этим id проверяет, что диалог собран целиком.
    public const int IdLevelLabel = 1101;
    public const int IdChunkLabel = 1102;
    public const int IdChunkRange = 1103;
    public const int IdScopeNote = 1104;
    public const int IdStoreNote = 1105;
    public const int IdChunkNote = 1106; // «чанк из настроек вне списка кнопок»: предупреждение, не ошибка

    // Стили и сообщения — значения из Windows SDK (10.0.26100.0\um\winuser.h).
    private const uint CsRedraw = 0x0003;                 // CS_HREDRAW | CS_VREDRAW
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsTabstop = 0x00010000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysmenu = 0x00080000;
    private const uint WsGroup = 0x00020000;             // начало группы контролов (нужно радио-группе)
    private const uint WsExDlgModalFrame = 0x00000001;
    private const uint CbsDropdownList = 0x0003;
    private const uint CbsAutoHscroll = 0x0040;
    private const uint CbsHasStrings = 0x0200;
    private const uint CbsNoIntegralHeight = 0x0400;
    private const uint SsNoPrefix = 0x0080;               // «&» в подписях — обычный символ, не горячая клавиша
    private const uint BsDefPushbutton = 0x0001;
    private const uint BsAutoRadiobutton = 0x0009;        // радио-кнопка: Windows сам снимает отметку с соседей
    private const uint WmClose = 0x0010;                 // обрабатывает DefWindowProc: он сам разрушает окно
    private const uint WmDestroy = 0x0002;
    private const uint WmSetText = 0x000C;
    private const uint WmSetFont = 0x0030;
    private const uint WmKeyDown = 0x0100;
    private const uint WmCommand = 0x0111;
    private const uint DmGetDefId = 0x0400;              // WM_USER + 0: «какая кнопка здесь главная»
    private const uint CbAddString = 0x0143;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbSetCurSel = 0x014E;
    private const uint BmGetCheck = 0x00F0;               // BM_GETCHECK: какая из радио-кнопок отмечена
    private const uint BmSetCheck = 0x00F1;               // BM_SETCHECK: отметить кнопку при открытии
    private const int BstChecked = 1;                     // BST_CHECKED
    private const int BnClicked = 0;
    private const int HasDefId = 0x534B;                 // DC_HASDEFID: старшие 16 бит ответа DM_GETDEFID
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int IdcArrow = 32512;                  // MAKEINTRESOURCEW(IDC_ARROW)
    private const int ColorWindow = 5;                   // COLOR_WINDOW — фон окна настроек
    private const int SwShow = 5;
    private const uint WmMousewheel = 0x020A;            // WM_MOUSEWHEEL: щелчок колёсика мыши
    private const uint CbGetCount = 0x0146;              // CB_GETCOUNT: сколько пунктов в списке уровней
    private const uint CbGetDroppedState = 0x0157;       // CB_GETDROPPEDSTATE: список раскрыт или нет
    private const int WheelDelta = 120;                  // WHEEL_DELTA: один щелчок колёсика
    private const string ComboListClass = "ComboLBox";   // класс окна раскрытого списка комбосписка
    private const int SpiGetNonClientMetrics = 0x0029;   // SPI_GETNONCLIENTMETRICS: системные шрифты окон

    private static IntPtr _dialog;
    private static IntPtr _owner;
    private static IntPtr _levelCombo;

    /// <summary>Шесть радиокнопок размера чанка по порядку PackerSettings.ChunkChoices.</summary>
    private static readonly IntPtr[] _chunkRadios = new IntPtr[PackerSettings.ChunkChoices.Length];

    private static IntPtr _note;
    private static IntPtr _ok;
    private static IntPtr _cancel;
    private static IntPtr _font;
    private static bool _closed;
    private static bool _busy;
    private static bool _ownerDisabled;

    /// <summary>
    /// Показывает диалог и возвращает управление только когда он закрыт. Вызывается из экспорта
    /// ConfigurePacker, то есть из кода Total Commander — наружу отсюда ничего не летит.
    /// </summary>
    public static void Show(IntPtr parent, IntPtr instance)
    {
        if (_busy)
        {
            PluginTrace.Write("ConfigurePacker: диалог уже открыт — повторный вызов проигнорирован");
            return;
        }

        _busy = true;
        _closed = false;

        try
        {
            Run(parent, instance);
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ConfigurePacker: диалог не открылся: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RestoreOwner();

            if (_dialog != IntPtr.Zero)
            {
                DestroyWindow(_dialog);
                _dialog = IntPtr.Zero;
            }

            if (_font != IntPtr.Zero)
            {
                DeleteObject(_font);
                _font = IntPtr.Zero;
            }

            // Класс окна нужен только на время диалога. В процессе TC его оставлять нельзя:
            // после перечитывания плагина класс указывал бы на уже выгруженный код.
            UnregisterClass(ClassName, instance);

            _levelCombo = _note = _ok = _cancel = IntPtr.Zero;
            Array.Clear(_chunkRadios, 0, _chunkRadios.Length);
            _owner = IntPtr.Zero;
            _closed = false;
            _busy = false;
        }
    }

    private static void Run(IntPtr parent, IntPtr instance)
    {
        if (!RegisterWindowClass(instance))
            return;

        PackerSettings.Settings settings = PackerSettings.Read();

        int dpi = ReadDpi(parent);

        // Шрифт надписей — такого же размера, как шрифт заголовка окна. Размер берём из системных
        // метрик Windows (lfCaptionFont): то же самое число пикселей, что и в строке заголовка,
        // а не придуманное плагином. Прежний размер был заметно мельче заголовка.
        int fontSize = CaptionFontSize(dpi);

        _font = CreateFont(fontSize);
        _owner = parent;
        MeasureFontMetrics(fontSize);

        int baseX = _baseX;
        int baseY = _baseY;
        int margin = Math.Max(baseX, 8);
        _margin = margin;

        // Тексты диалога. Длинные строки перенесены вручную: STATIC переносит плохо, а ширина окна
        // ниже считается по измеренным строкам, поэтому при крупном шрифте ничего не обрезается.
        string levelLabel = "Уровень сжатия нового контейнера: 1 — быстро, 22 — максимально медленно";
        string chunkLabel = "Размер чанка нового контейнера";
        string chunkRange =
            "Любой другой размер чанка (64 КиБ..16 МиБ) задаётся в командной строке: Container_V3.exe pack -chunk N";
        string scopeNote =
            "Уровень сжатия и размер чанка записываются в заголовок контейнера при его создании.\n" +
            "У уже готового контейнера они не меняются: при добавлении файлов остаются такими,\n" +
            "какими были при первой упаковке этого контейнера.";
        string storeNote = $"Настройки сохраняются в: {PackerSettings.StorePath}";

        (int KiB, string Caption)[] chunkChoices = PackerSettings.ChunkChoices;
        int[] radioWidths = new int[chunkChoices.Length];
        int radioGap = baseX;
        int radioRowWidth = 0;

        for (int index = 0; index < chunkChoices.Length; index++)
        {
            // Подпись кнопки плюс сам кружок радио: его размер зависит от шрифта.
            radioWidths[index] = MeasureTextWidth(chunkChoices[index].Caption) + baseY + 8;
            radioRowWidth += radioWidths[index];
        }

        radioRowWidth += (chunkChoices.Length - 1) * radioGap;

        int columns = radioRowWidth;
        columns = Math.Max(columns, MeasureTextWidth(levelLabel));
        columns = Math.Max(columns, MeasureTextWidth(chunkLabel));
        columns = Math.Max(columns, MeasureTextWidth(chunkRange));
        columns = Math.Max(columns, MeasureTextWidth(scopeNote));
        columns = Math.Max(columns, MeasureTextWidth(storeNote));
        columns = Math.Max(columns, 24 * baseX); // окно не должно быть уже, чем место под кнопки

        // Ближайшая кнопка к тому, что лежит в настройках. Если в ini размер, которого среди кнопок
        // нет (например 128 КиБ из командной строки), отмечена ближайшая кнопка, а прежнее значение
        // показано отдельной строкой: пользователь его видит и сам решает, менять его или нет.
        int chunkChoice = PackerSettings.NearestChunkKiB(settings.ChunkKiB);
        string chunkNote = settings.ChunkKiB == chunkChoice
            ? string.Empty
            : $"В сохранённых настройках чанк {settings.ChunkKiB} КиБ — такой кнопки нет. " +
              $"Отмечен ближайший ({chunkChoice} КиБ); «OK» запишет именно его.";

        const int comboLines = 12;      // открытый список уровней: 22 пункта, со прокруткой
        const int comboRow = 1;
        const int chunkLabelRow = 3;    // 2 — пустая строка: закрытый список занимает только свою строку
        const int chunkRadioRow = 4;
        const int chunkRangeRow = 6;    // 5 — пустая строка между кнопками чанка и строкой про командную строку
        const int chunkNoteRow = 7;
        const int scopeNoteRow = 8;     // 8..10 — три строки пояснения
        const int storeNoteRow = 11;
        const int noteRow = 12;         // строка ошибки: пустая, пока диалог не ругался
        const int buttonRow = 14;

        int radioHeight = baseY + baseY / 3;
        int buttonHeight = baseY + baseY / 2;
        int buttonWidth = Math.Max(MeasureTextWidth("Отмена") + 2 * baseX, 10 * baseX);

        int clientWidth = columns + 2 * margin;
        int clientHeight = buttonRow * baseY + buttonHeight + 2 * margin;

        var client = new Rect { _left = 0, _top = 0, _right = clientWidth, _bottom = clientHeight };
        AdjustWindowRectEx(ref client, WsPopup | WsCaption | WsSysmenu, 0, WsExDlgModalFrame);
        int windowWidth = client._right - client._left;
        int windowHeight = client._bottom - client._top;

        if (!TryStartPosition(parent, windowWidth, windowHeight, out int x, out int y))
        {
            PluginTrace.Write("ConfigurePacker: не удалось подобрать место для диалога");
            return;
        }

        fixed (char* className = ClassName)
        fixed (char* caption = Caption)
        {
            _dialog = CreateWindowExW(
                WsExDlgModalFrame, className, caption,
                WsPopup | WsCaption | WsSysmenu, // без WS_VISIBLE: окно показываем после разметки
                x, y, windowWidth, windowHeight,
                parent, IntPtr.Zero, instance, IntPtr.Zero);
        }

        if (_dialog == IntPtr.Zero)
        {
            PluginTrace.Write($"ConfigurePacker: окно не создано, win32 {Marshal.GetLastPInvokeError()}");
            return;
        }

        AddStatic(levelLabel, X(0), Y(0), columns, baseY, IdLevelLabel);

        _levelCombo = CreateControl("COMBOBOX", string.Empty,
            CbsDropdownList | CbsAutoHscroll | CbsHasStrings | CbsNoIntegralHeight | WsTabstop, 0,
            X(0), Y(comboRow), columns, comboLines * baseY, IdLevelCombo);
        AddLevelItems(_levelCombo, settings.Level);

        AddStatic(chunkLabel, X(0), Y(chunkLabelRow), columns, baseY, IdChunkLabel);

        int radioX = X(0);
        for (int index = 0; index < chunkChoices.Length; index++)
        {
            // Первый радио — начало группы (WS_GROUP вместе с WS_TABSTOP): Tab заходит в группу один
            // раз, а по кнопкам внутри неё двигают стрелки. Остальные без WS_TABSTOP именно поэтому.
            uint radioStyle = BsAutoRadiobutton | (index == 0 ? WsTabstop | WsGroup : 0);

            _chunkRadios[index] = CreateControl("BUTTON", chunkChoices[index].Caption,
                radioStyle, 0, radioX, Y(chunkRadioRow), radioWidths[index], radioHeight,
                IdChunkRadioFirst + index);

            SendMessageW(_chunkRadios[index], BmSetCheck,
                (IntPtr)(chunkChoices[index].KiB == chunkChoice ? BstChecked : 0), IntPtr.Zero);

            radioX += radioWidths[index] + radioGap;
        }

        AddStatic(chunkRange, X(0), Y(chunkRangeRow), columns, baseY, IdChunkRange);
        AddStatic(chunkNote, X(0), Y(chunkNoteRow), columns, baseY, IdChunkNote);

        AddStatic(scopeNote, X(0), Y(scopeNoteRow), columns, 3 * baseY, IdScopeNote);
        AddStatic(storeNote, X(0), Y(storeNoteRow), columns, baseY, IdStoreNote);

        _note = AddStatic(string.Empty, X(0), Y(noteRow), columns, baseY, IdNote);

        int cancelX = X(0) + columns - buttonWidth;
        int okX = cancelX - buttonWidth - baseX;

        _ok = CreateControl("BUTTON", "OK", BsDefPushbutton | WsTabstop | WsGroup, 0,
            okX, Y(buttonRow), buttonWidth, buttonHeight, IdOk);
        _cancel = CreateControl("BUTTON", "Отмена", 0, 0,
            cancelX, Y(buttonRow), buttonWidth, buttonHeight, IdCancel);

        ShowWindow(_dialog, SwShow);
        BringWindowToTop(_dialog);
        SetForegroundWindow(_dialog);
        SetFocus(_levelCombo);

        if (parent != IntPtr.Zero)
        {
            // Модальность: пока открыт диалог настроек, окном упаковывания TC не пользуются.
            EnableWindow(parent, 0);
            _ownerDisabled = true;
        }

        while (!_closed)
        {
            // hWnd = NULL, фильтр 0..0: берём сообщения всех окон этого потока, а не только своего
            // окна и его детей (смысл фильтра — в remark к GetMessage). Это и чинит зависание,
            // которое видел пользователь: клик мышью по раскрытому списку уровней. Раскрытый список
            // Windows держит в отдельном окне «ComboLBox» — оно принадлежит потоку Total Commander,
            // но дочерним для нашего окна не является. Со старым фильтром его мышиные сообщения
            // никогда не забирались из очереди, комбосписок захватывал мышь и ждал их, а окно
            // выглядело зависшим. Колёсико старым фильтром проходило: его сообщения адресовались
            // нашему комбосписку, но Windows умел только прокручивать раскрытый список — сам
            // выбранный уровень колёсиком не менялся (об этом и просил пользователь).
            int result = GetMessageW(out Message message, IntPtr.Zero, 0, 0);
            if (result <= 0)
            {
                // 0 — WM_QUIT: его прислал не наш диалог (поток закрывают сам TC или Windows).
                // Проглотить его нельзя, иначе цикл Total Commander потеряет запрос на закрытие:
                // ставим сообщение в очередь заново. -1 — ошибка GetMessageW.
                if (result == 0)
                    PostQuitMessage(0);

                PluginTrace.Write($"ConfigurePacker: GetMessageW вернул {result} — диалог закрывается");
                break;
            }

            // Колёсико мыши над комбосписком уровней обрабатываем сами. Windows по умолчанию реагирует
            // на него только в раскрытом списке и только прокручивает его: если выбранный уровень и так
            // виден наверху, крутить вверх некуда, а закрытый комбосписок на колёсико не отвечает вовсе.
            // Здесь один щелчок колёсика = на один уровень вверх или вниз; если упёрлись в край списка,
            // сообщение отдаём Windows, чтобы он прокручил раскрытый список.
            if (message._message == WmMousewheel && IsLevelWheelTarget(message._hwnd))
            {
                if (MoveLevelByWheel(message._wParam))
                    continue;
            }

            // Сообщения нашего окна и его контролов прогоняем через IsDialogMessageW — от него
            // работают Tab, стрелки в радио-группе и Enter на кнопке по умолчанию. Сообщения чужих
            // окон TC только translate + dispatch: модальный цикл их не перехватывает и не присваивает.
            bool ours = IsOwnWindow(message._hwnd);

            if (ours && message._message == WmKeyDown)
            {
                int vk = unchecked((int)message._wParam.ToInt64() & 0xFFFF);

                if (vk == VkEscape)
                {
                    CloseDialog();
                    continue;
                }

                // Enter без фокуса на контроле = «OK». Когда фокус на контроле, Enter обрабатывает
                // IsDialogMessageW по кнопке по умолчанию (DM_GETDEFID).
                if (vk == VkReturn && GetFocus() == IntPtr.Zero)
                {
                    TryAccept();
                    continue;
                }
            }

            if (ours && IsDialogMessageW(_dialog, ref message) != 0)
                continue;

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// Сообщение адресовано нашему диалогу или его контролам? Поднимаемся по цепочке родителей:
    /// у наших контролов родитель — окно диалога, у чужих окон Total Commander — свои родители,
    /// и к нам они не ведут.
    /// </summary>
    private static bool IsOwnWindow(IntPtr window) => IsWindowUnder(window, _dialog);

    /// <summary>То же, но для произвольного «хозяина»: окно — сам хозяин или его потомок.</summary>
    private static bool IsWindowUnder(IntPtr window, IntPtr owner)
    {
        if (owner == IntPtr.Zero)
            return false;

        for (IntPtr current = window; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == owner)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Колесо мышки относится к списку уровней? Это либо сам комбосписок (закрытый или раскрытый),
    /// либо окно раскрытого списка. Харнесс показал, что окно раскрытого списка Windows создаёт
    /// отдельным окном класса ComboLBox, и его цепочка родителей к нашему диалогу не ведёт — поэтому
    /// список опознаём по классу окна, а не по подчинённости. Колёсико чужих окон остаётся чужим:
    /// сообщению, адресованному окну другого процесса, в нашей очереди просто не бывать.
    /// </summary>
    private static bool IsLevelWheelTarget(IntPtr window)
        => IsWindowUnder(window, _levelCombo) || (IsLevelDropdownOpen() && IsComboListWindow(window));

    private static bool IsLevelDropdownOpen()
        => _levelCombo != IntPtr.Zero
            && SendMessageW(_levelCombo, CbGetDroppedState, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;

    /// <summary>Оно окно раскрытого списка комбосписка? Класс такого окна — ComboLBox (проверено харнессом).</summary>
    private static bool IsComboListWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        Span<char> className = stackalloc char[16]; // «ComboLBox» — 9 символов, с запасом
        fixed (char* buffer = className)
        {
            int length = GetClassNameW(window, buffer, className.Length);
            return length > 0 && className.Slice(0, length).SequenceEqual(ComboListClass);
        }
    }

    /// <summary>
    /// Щелчок колёсика над комбосписком уровней: сдвинуть выбранный уровень на величину щелчка.
    /// В wParam старшие 16 бит — величина: WHEEL_DELTA на один щелчок, колесо «от себя» — со знаком
    /// минус (то есть к более поздним пунктам списка). Возвращает false, если сдвигать некуда: тогда
    /// колесо отдаётся Windows, и он прокручивает раскрытый список как умеет.
    /// </summary>
    private static bool MoveLevelByWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

        if (delta == 0 || _levelCombo == IntPtr.Zero)
            return false;

        int count = (int)SendMessageW(_levelCombo, CbGetCount, IntPtr.Zero, IntPtr.Zero).ToInt64();
        int current = (int)SendMessageW(_levelCombo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt64();

        if (count <= 0)
            return false;

        if (current < 0)
            current = delta < 0 ? -1 : count;  // ничего не выбрано: первый щелчок выбирает край списка

        int notches = Math.Max(1, Math.Abs(delta) / WheelDelta);
        int target = current + (delta < 0 ? notches : -notches);

        if (target < 0 || target >= count)
            return false; // упёрлись в край списка: пусть колесо прокручивает сам список

        SendMessageW(_levelCombo, CbSetCurSel, (IntPtr)target, IntPtr.Zero);
        return true;
    }

    /// <summary>Тело оконного сообщения. Обычный управляемый метод: native-вход — это WndProc ниже.</summary>
    private static IntPtr WndProcBody(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case DmGetDefId:
                // Сообщаем, что главная кнопка — «OK» (на неё и реагирует Enter).
                return (IntPtr)((HasDefId << 16) | IdOk);

            case WmCommand:
                int id = unchecked((int)(wParam.ToInt64() & 0xFFFF));
                int code = unchecked((int)((wParam.ToInt64() >> 16) & 0xFFFF));

                if (code == BnClicked && id == IdOk)
                {
                    TryAccept();
                    return (IntPtr)1;
                }

                if (code == BnClicked && id == IdCancel)
                {
                    CloseDialog();
                    return (IntPtr)1;
                }

                return IntPtr.Zero;

            case WmDestroy:
                _closed = true;
                return IntPtr.Zero;
        }

        // Остальное — в DefWindowProc: он и рисует controls, и разрушает окно по WM_CLOSE.
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// «OK»: взять выбранный уровень и отмеченную кнопку чанка, сохранить настройки и закрыть диалог.
    /// Если что-то не выбрано, диалог остаётся открытым и показывает, чего не хватает.
    /// </summary>
    private static void TryAccept()
    {
        int index = unchecked((int)SendMessageW(_levelCombo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt64());
        (int Level, string Caption)[] levels = PackerSettings.Levels;

        if (index < 0 || index >= levels.Length)
        {
            ShowError("Выберите уровень сжатия из списка.");
            return;
        }

        int level = levels[index].Level;
        int chunkKiB = CheckedChunkKiB();

        if (chunkKiB == 0)
        {
            ShowError("Выберите размер чанка — одну из шести кнопок.");
            return;
        }

        if (!PackerSettings.Save(level, chunkKiB, out string store))
        {
            ShowError($"Не удалось сохранить настройки в файл «{store}».");
            return;
        }

        PluginTrace.Write($"ConfigurePacker: сохранено — уровень {level}, чанк {chunkKiB} КиБ, файл «{store}»");
        CloseDialog();
    }

    /// <summary>
    /// Размер чанка по отмеченной радио-кнопке; 0 — если ни одна не отмечена. Проверяем по очереди
    /// все кнопки, а не верим Windows на слово: BS_AUTORADIOBUTTON снимает отметку с соседей сам, но
    /// сбой здесь означал бы, что в контейнер записался не тот размер, который видит пользователь.
    /// </summary>
    private static int CheckedChunkKiB()
    {
        (int KiB, string Caption)[] choices = PackerSettings.ChunkChoices;
        int checkedCount = 0;
        int value = 0;

        for (int index = 0; index < _chunkRadios.Length; index++)
        {
            if (_chunkRadios[index] == IntPtr.Zero)
                continue;

            if (SendMessageW(_chunkRadios[index], BmGetCheck, IntPtr.Zero, IntPtr.Zero).ToInt64() != 0)
            {
                checkedCount++;
                value = choices[index].KiB;
            }
        }

        // Две отмеченные кнопки невозможны при BS_AUTORADIOBUTTON; если всё-таки так — не угадываем.
        return checkedCount == 1 ? value : 0;
    }

    private static void ShowError(string message)
    {
        PluginTrace.Write($"ConfigurePacker: {message}");
        SetText(_note, message);
    }

    /// <summary>
    /// Выход из диалога как EndDialog: помечаем закрытие и позволяем цикл сообщений завершиться
    /// самому. Окно уничтожает Show после выхода из цикла.
    ///
    /// DestroyWindow прямо здесь выглядело бы логично, но обработчик сообщений часто идёт вложенно
    /// внутри чужого SendMessage (так кнопку нажимает сам Windows или харнесс). Уничтожить окно
    /// в такой момент — значит оборвать эту цепочку: нажатие кнопки не доходит до конца, а поток
    /// пакера остаётся ждать. Поэтому здесь только флаг, а окно — потом и на своём месте.
    /// </summary>
    private static void CloseDialog()
    {
        // Выход как у EndDialog: окно закрываем сообщением, а не DestroyWindow прямо из
        // обработчика кнопки. Кнопку Windows (и харнесс) доставляет вложенным SendMessage, и
        // DestroyWindow в такой момент оставляет эту цепочку оборванной, а цикл — спящим на
        // GetMessageW уже уничтоженного окна. WM_CLOSE будит цикл и уничтожает окно сам
        // DefWindowProc; WM_DESTROY ставит _closed, и цикл выходит после DispatchMessageW.
        if (_dialog != IntPtr.Zero)
            PostMessageW(_dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static void RestoreOwner()
    {
        if (_ownerDisabled && _owner != IntPtr.Zero)
        {
            EnableWindow(_owner, 1);
            _ownerDisabled = false;
        }
    }

    private static bool RegisterWindowClass(IntPtr instance)
    {
        fixed (char* className = ClassName)
        {
            var info = new WndClassExW
            {
                _cbSize = (uint)sizeof(WndClassExW),
                _style = CsRedraw,
                _lpfnWndProc = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                _cbClsExtra = 0,
                _cbWndExtra = 0,
                _hInstance = instance,
                _hIcon = IntPtr.Zero,
                _hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IdcArrow),
                _hbrBackground = GetSysColorBrush(ColorWindow),
                _lpszMenuName = IntPtr.Zero,
                _lpszClassName = className,
                _hIconSm = IntPtr.Zero,
            };

            if (RegisterClassExW(ref info) != 0)
                return true;

            // Класс мог остаться после предыдущего показа — снимаем и пробуем ещё раз.
            UnregisterClass(ClassName, instance);

            if (RegisterClassExW(ref info) != 0)
                return true;

            PluginTrace.Write($"ConfigurePacker: RegisterClassExW не удалась, win32 {Marshal.GetLastPInvokeError()}");
            return false;
        }
    }

    private static void UnregisterClass(string className, IntPtr instance)
    {
        fixed (char* name = className)
            UnregisterClassW(name, instance);
    }

    private static void AddLevelItems(IntPtr combo, int currentLevel)
    {
        if (combo == IntPtr.Zero)
            return;

        (int Level, string Caption)[] levels = PackerSettings.Levels;
        int selectedIndex = -1;

        for (int index = 0; index < levels.Length; index++)
        {
            fixed (char* caption = levels[index].Caption)
                SendMessageW(combo, CbAddString, IntPtr.Zero, (IntPtr)caption);

            if (levels[index].Level == currentLevel)
                selectedIndex = index;
        }

        if (selectedIndex >= 0)
            SendMessageW(combo, CbSetCurSel, (IntPtr)selectedIndex, IntPtr.Zero);
    }

    private static IntPtr AddStatic(string text, int x, int y, int width, int height, int id = 0)
        => CreateControl("STATIC", text, SsNoPrefix, 0, x, y, width, height, id);

    /// <param name="id">
    /// Идентификатор контрола: для дочернего окна это параметр hMenu CreateWindowExW. Без него контрол
    /// не имеет id, и тогда GetDlgItem его не находит, а WM_COMMAND от кнопки приходит с нулём вместо
    /// IDOK/IDCANCEL — то есть кнопки просто не работают.
    /// </param>
    private static IntPtr CreateControl(
        string className, string text, uint style, uint exStyle, int x, int y, int width, int height, int id)
    {
        IntPtr control;

        fixed (char* classNamePtr = className)
        fixed (char* textPtr = text)
        {
            control = CreateWindowExW(
                exStyle, classNamePtr, textPtr,
                WsChild | WsVisible | style,
                x, y, width, height,
                _dialog, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
        }

        if (control == IntPtr.Zero)
        {
            PluginTrace.Write($"ConfigurePacker: control «{className}» не создан, win32 {Marshal.GetLastPInvokeError()}");
            return IntPtr.Zero;
        }

        if (_font != IntPtr.Zero)
            SendMessageW(control, WmSetFont, _font, (IntPtr)1);

        return control;
    }

    private static void SetText(IntPtr control, string text)
    {
        if (control == IntPtr.Zero)
            return;

        fixed (char* ptr = text)
            SendMessageW(control, WmSetText, IntPtr.Zero, (IntPtr)ptr);
    }

    private static bool TryStartPosition(IntPtr parent, int width, int height, out int x, out int y)
    {
        if (parent != IntPtr.Zero && GetWindowRect(parent, out Rect owner) != 0)
        {
            x = owner._left + ((owner._right - owner._left) - width) / 2;
            y = owner._top + ((owner._bottom - owner._top) - height) / 2;
            return true;
        }

        int screenWidth = GetSystemMetrics(0);  // SM_CXSCREEN
        int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN

        x = Math.Max(0, (screenWidth - width) / 2);
        y = Math.Max(0, (screenHeight - height) / 2);
        return screenWidth > 0 && screenHeight > 0;
    }

    private static int ReadDpi(IntPtr parent)
    {
        int dpi = parent != IntPtr.Zero
            ? (int)GetDpiForWindow(parent)
            : (int)GetSystemDpiForProcess();

        return dpi <= 0 ? 96 : dpi;
    }

    /// <summary>
    /// Размер подписей = размер шрифта заголовка окна: Windows отдаёт его в системных метриках окон
    /// (NONCLIENTMETRICS.lfCaptionFont), тем самым шрифтом она рисует строку заголовка. Берём именно
    /// величину знака, а не сам шрифт целиком: в заголовке Windows рисует его полужирнее, а подписям
    /// обычное начертание подходит больше. Высоту lfCaptionFont Windows отдаёт для плотности главного
    /// экрана, поэтому пересчитываем её в плотность окна диалога: 12 пикселей при 100 % и 18 при 150 %.
    /// </summary>
    private static int CaptionFontSize(int windowDpi)
    {
        NonClientMetrics metrics = new NonClientMetrics();
        metrics.cbSize = (uint)Unsafe.SizeOf<NonClientMetrics>();

        int ok = SystemParametersInfoW(SpiGetNonClientMetrics, 0, ref metrics, 0);
        int captionHeight = ok != 0 ? Math.Abs(metrics.lfCaptionFont.lfHeight) : 0;

        if (captionHeight <= 0)
        {
            // Системные метрики не получены: берём Segoe UI 9 pt — именно столько и составляет
            // шрифт заголовка в Windows 10/11 при обычной плотности.
            PluginTrace.Write("ConfigurePacker: системный шрифт заголовка не получен, берём Segoe UI 9 pt");
            return Math.Max(12, windowDpi * 12 / 96);
        }

        int systemDpi = (int)GetDpiForWindow(GetDesktopWindow());
        int fontSize = systemDpi > 0 ? captionHeight * windowDpi / systemDpi : captionHeight;

        PluginTrace.Write($"ConfigurePacker: размер шрифта как в заголовке окна — {fontSize} px (DPI окна {windowDpi}, DPI системы {systemDpi})");
        return Math.Max(8, fontSize);
    }

    private static IntPtr CreateFont(int fontSize)
    {
        fixed (char* face = "Segoe UI")
            return CreateFontW(
                -fontSize, 0, 0, 0, 400 /* FW_NORMAL */,
                0, 0, 0,
                1 /* DEFAULT_CHARSET */, 0 /* OUT_DEFAULT_PRECIS */, 0 /* CLIP_DEFAULT_PRECIS */,
                5 /* CLEARTYPE_QUALITY */, 0 /* DEFAULT_PITCH | FF_DONTCARE */,
                face);
    }

    private static int X(int column) => _margin + column * _baseX;
    private static int Y(int row) => _margin + row * _baseY;

    /// <summary>
    /// Единицы разметки — из метрик того шрифта, который реально создали (GetTextMetricsW), а не из
    /// приблизительной формулы: при крупном шрифте и нестандартном DPI оценка ошибается, и подписи
    /// начало бы обрезаться. baseX — средняя ширина символа, baseY — высота строки с внешним интервалом.
    /// </summary>
    private static void MeasureFontMetrics(int fontSize)
    {
        IntPtr dc = GetDC(IntPtr.Zero);

        if (dc != IntPtr.Zero)
        {
            TextMetric metrics;
            IntPtr previous = SelectObject(dc, _font);
            int ok = GetTextMetricsW(dc, out metrics);
            SelectObject(dc, previous);
            ReleaseDC(IntPtr.Zero, dc);

            if (ok != 0 && metrics._tmAveCharWidth > 0 && metrics._tmHeight > 0)
            {
                _baseX = metrics._tmAveCharWidth;
                _baseY = metrics._tmHeight + metrics._tmExternalLeading;
                return;
            }
        }

        // Запасной расчёт, если DC не выдан (например, без рабочего стола): та же оценка, что была раньше.
        _baseX = Math.Max(5, fontSize * 7 / 12);
        _baseY = Math.Max(13, fontSize * 4 / 3);
        PluginTrace.Write("ConfigurePacker: метрики шрифта не получены, разметка по оценке");
    }

    /// <summary>
    /// Ширина самой длинной строки текста этим шрифтом (GetTextExtentPoint32W). Переносы \n учитываются:
    /// multiline-подписи измеряются по строкам, потому что Static рисует их именно так.
    /// </summary>
    private static int MeasureTextWidth(string text)
    {
        if (_font == IntPtr.Zero || text.Length == 0)
            return 0;

        IntPtr dc = GetDC(IntPtr.Zero);

        if (dc == IntPtr.Zero)
            return text.Length * _baseX;

        IntPtr previous = SelectObject(dc, _font);
        int widest = 0;

        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            fixed (char* ptr = line)
            {
                if (GetTextExtentPoint32W(dc, ptr, line.Length, out Size extent) != 0 && extent._cx > widest)
                    widest = extent._cx;
            }
        }

        SelectObject(dc, previous);
        ReleaseDC(IntPtr.Zero, dc);

        return widest;
    }

    // Единицы разметки посчитаны один раз на показ диалога (по DPI окна владельца и нашему шрифту).
    private static int _baseX;
    private static int _baseY;
    private static int _margin;

    // Оконное сообщение: native-вход, на него указывает lpfnWndProc класса окна.
    // CallConvs указана явно: без неё Roslyn не считает WndProc совместимым с
    // delegate* unmanaged[Cdecl] при &(WndProc) (CS8757).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => WndProcBody(hWnd, msg, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint _cbSize;
        public uint _style;
        public IntPtr _lpfnWndProc;
        public int _cbClsExtra;
        public int _cbWndExtra;
        public IntPtr _hInstance;
        public IntPtr _hIcon;
        public IntPtr _hCursor;
        public IntPtr _hbrBackground;
        public IntPtr _lpszMenuName;
        public char* _lpszClassName;
        public IntPtr _hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr _hwnd;
        public uint _message;
        public IntPtr _wParam;
        public IntPtr _lParam;
        public int _time;
        public int _ptX;
        public int _ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int _left;
        public int _top;
        public int _right;
        public int _bottom;
    }

    /// <summary>TEXTMETRICW из wingdi.h — поля и в том же порядке, что у нативной структуры.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetric
    {
        public int _tmHeight;
        public int _tmAscent;
        public int _tmDescent;
        public int _tmInternalLeading;
        public int _tmExternalLeading;
        public int _tmAveCharWidth;
        public int _tmMaxCharWidth;
        public int _tmWeight;
        public int _tmOverhang;
        public int _tmDigitizedAspectX;
        public int _tmDigitizedAspectY;
        public char _tmFirstChar;
        public char _tmLastChar;
        public char _tmDefaultChar;
        public char _tmBreakChar;
        public byte _tmItalic;
        public byte _tmUnderlined;
        public byte _tmStruckOut;
        public byte _tmPitchAndFamily;
        public byte _tmCharSet;
    }

    /// <summary>SIZE из windef.h (его возвращает GetTextExtentPoint32W).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int _cx;
        public int _cy;
    }

    /// <summary>LOGFONTW из wingdi.h (92 байта): LF_FACESIZE = 32 символа в имени шрифта.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
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
        public fixed char lfFaceName[32];
    }

    /// <summary>
    /// NONCLIENTMETRICSW из winuser.h (504 байта): размеры рамок окна и системные шрифты — тем же
    /// шрифтом Windows рисует заголовок окна, меню и пояснения в диалогах. Порядок полей именно как
    /// в заголовке: после lfMenuFont идут lfStatusFont и только затем lfMessageFont.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NonClientMetrics
    {
        public uint cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;
        public LogFont lfCaptionFont;
        public int iSmCaptionWidth;
        public int iSmCaptionHeight;
        public LogFont lfSmCaptionFont;
        public int iMenuWidth;
        public int iMenuHeight;
        public LogFont lfMenuFont;
        public LogFont lfStatusFont;
        public LogFont lfMessageFont;
        public int iPaddedBorderWidth; // WINVER >= 0x0600
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW classInfo);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int UnregisterClassW(char* className, IntPtr instance);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetMessageW(out Message message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int IsDialogMessageW(IntPtr dialog, ref Message message);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int TranslateMessage(ref Message message);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int DestroyWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int BringWindowToTop(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int EnableWindow(IntPtr window, int enable);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowRect(IntPtr window, out Rect rect);

    /// <summary>Родитель окна (для popup — его владелец): нужен, чтобы понять, наше ли это окно.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr window);

    /// <summary>Имя класса окна: им опознаётся раскрытый список комбосписка (ComboLBox).</summary>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, char* className, int maxCount);

    /// <summary>Ставит WM_QUIT в очередь потока. Нужен, чтобы чужой WM_QUIT не пропал.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int AdjustWindowRectEx(ref Rect rect, uint style, int hasMenu, uint exStyle);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetSysColorBrush(int index);

    /// <summary>DC всего экрана: нужен только чтобы измерить шрифт, рисовать на нём никто не будет.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetSystemDpiForProcess();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>SystemParametersInfoW: по uiAction = SPI_GETNONCLIENTMETRICS отдаёт NONCLIENTMETRICSW.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SystemParametersInfoW(int uiAction, int uiParam, ref NonClientMetrics param, int flags);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        char* faceName);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetTextMetricsW(IntPtr dc, out TextMetric metrics);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetTextExtentPoint32W(IntPtr dc, char* text, int length, out Size extent);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int DeleteObject(IntPtr obj);
}
