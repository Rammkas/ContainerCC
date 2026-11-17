using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Container_V3;
using Container_V3.Packing;

namespace ContainerWcx;

/// <summary>
/// Настройки пакера, которые пользователь выбирает в диалоге «Настроить» (ConfigurePacker):
/// уровень сжатия и размер чанка. Хэша здесь нет намеренно: в заголовке контейнера есть только
/// длина хэша, своего поля «метод хэша» у формата нет, а значит выбор метода сделал бы контейнеры,
/// которые старая версия плагина уже не откроет.
///
/// И уровень, и размер чанка — свойства контейнера: они записываются в его заголовок при создании.
/// Поэтому настройка действует только на НОВЫЕ контейнеры. При добавлении файлов в уже готовый
/// контейнер ContainerModifier берёт ChunkSize и CompressionLevel из его заголовка, и никакие
/// настройки диалога их не меняют — это проверка харнессом, а не оговорка «в принципе».
///
/// Где лежат настройки: Total Commander в PackSetDefaultParams подсказывает путь к ini пакеров
/// (у него это pkplugin.ini рядом с программой). Плагин пишет свою секцию [ContainerCC] туда —
/// так советует SDK («используйте уникальную секцию, файл общий с другими плагинами»). Если в этот
/// файл писать не получается (TC в Program Files без прав администратора), плагин переходит на свой
/// файл %APPDATA%\ContainerCC\ContainerCC.ini и пишет об этом в журнал. Чтение: сначала подсказанный
/// TC файл, затем — если ключа там нет — файл плагина; если и там нет, значение по умолчанию.
///
/// Значения из ini проверяются: вне диапазона или не число — берётся default, и в журнал попадает
/// строка о плохом значении. Битый ini не должен срывать упаковку.
/// </summary>
internal static class PackerSettings
{
    /// <summary>Нижняя граница уровня zstd, верхняя — максимум, который понимает zstd.</summary>
    public const int MinLevel = 1;
    public const int MaxLevel = 22;
    public const int DefaultLevel = ContainerFormat.DefaultCompressionLevel; // 3, как у командной строки

    public const int MinChunkKiB = ContainerFormat.MinChunkSize / 1024;   // 64 КиБ
    public const int MaxChunkKiB = ContainerFormat.MaxChunkSize / 1024;   // 16 МиБ
    public const int DefaultChunkKiB = ContainerFormat.DefaultChunkSize / 1024; // 1 МиБ

    /// <summary>Секция в ini. Имя уникальное: файл pkplugin.ini TC отдаёт всем плагинам сразу.</summary>
    public const string Section = "ContainerCC";

    private const string LevelKey = "CompressionLevel";
    private const string ChunkKey = "ChunkSizeKiB";

    /// <summary>Маркер «ключа нет»: GetPrivateProfileStringW возвращает именно это значение.</summary>
    private const string Unset = "\u0001нет значения\u0001";

    private static readonly object Gate = new();

    /// <summary>ini, который подсказал TC (DefaultIniName из PackSetDefaultParams).</summary>
    private static string? _iniFromTc;

    /// <summary>Файл, в который плагин реально пишет: подсказанный TC или собственный в %APPDATA%.</summary>
    private static string _store = string.Empty;

    /// <summary>
    /// Уровень сжатия и его подпись в диалоге. Пресеты не отдельный список, а подписи к своим
    /// числам: «быстро» — это уровень 1, «максимально» — 19. Так один список отвечает и тем, кто
    /// хочет просто выбрать пресет, и тем, кому нужен точный уровень.
    /// </summary>
    public static readonly (int Level, string Caption)[] Levels = BuildLevels();

    /// <summary>
    /// Фиксированные размеры чанка в диалоге — шесть кнопок. Диалог нарочно не даёт вписать «что
    /// угодно»: короткий выбор нельзя испортить опечаткой, и он сразу показывает разумные границы.
    /// Другие размеры внутри 64 КиБ..16 МиБ остаются доступны в командной строке
    /// (Container_V3.exe pack -chunk N): плагин их прочитает из ini как обычно, а диалог отметит
    /// ближайшую кнопку и отдельной строкой скажет, что именно лежало в настройках.
    /// Кнопки покрывают и границы, и default: MinChunkKiB, DefaultChunkKiB и MaxChunkKiB в списке есть.
    /// </summary>
    public static readonly (int KiB, string Caption)[] ChunkChoices =
    [
        (MinChunkKiB, "64 КиБ"),
        (256, "256 КиБ"),
        (DefaultChunkKiB, "1 МиБ"),
        (2048, "2 МиБ"),
        (8192, "8 МиБ"),
        (MaxChunkKiB, "16 МиБ"),
    ];

    private static (int Level, string Caption)[] BuildLevels()
    {
        string[] notes = new string[MaxLevel + 1];
        notes[1] = "быстро";
        notes[3] = "сбалансированно (по умолчанию)";
        notes[9] = "глубоко";
        notes[19] = "максимально";

        var levels = new (int, string)[MaxLevel - MinLevel + 1];
        for (int level = MinLevel; level <= MaxLevel; level++)
        {
            string? note = notes[level];
            levels[level - MinLevel] = note is null
                ? (level, level.ToString(CultureInfo.InvariantCulture))
                : (level, $"{level} — {note}");
        }

        return levels;
    }

    /// <summary>
    /// Ближайшая кнопка чанка для значения, которое лежит в настройках, но среди кнопок его нет.
    /// Расстояние относительное: 2 МиБ к 1 МиБ ближе, чем к 64 КиБ, хотя по абсолютной разнице
    /// наоборот. При одинаковом расстоянии (128 КиБ одинаково далеко от 64 КиБ и от 256 КиБ)
    /// выбирается меньший размер: он и упаковывает быстрее, и не делает контейнер неожиданно большим.
    /// </summary>
    public static int NearestChunkKiB(int chunkKiB)
    {
        (int KiB, string Caption)[] choices = ChunkChoices;
        int best = choices[0].KiB;
        long bestGap = ChunkGap(chunkKiB, best);

        foreach ((int KiB, string) choice in choices)
        {
            long gap = ChunkGap(chunkKiB, choice.KiB);

            // Строгое «меньше»: при равенстве остаётся тот, что встретился раньше, а кнопки идут от меньшего к большему.
            if (gap < bestGap)
            {
                best = choice.KiB;
                bestGap = gap;
            }
        }

        return best;
    }

    /// <summary>Отношение большего размера к меньшему, умноженное на 1000 (1000 = «равны»).</summary>
    private static long ChunkGap(int value, int choice)
    {
        long lo = Math.Min(value, choice);
        long hi = Math.Max(value, choice);

        return lo <= 0 ? long.MaxValue / 2 : hi * 1000 / lo;
    }

    /// <summary>Путь к ini, который показал TC (вызывается из PackSetDefaultParams).</summary>
    public static void RememberTcIni(string? path)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string normalized = path.Trim();
            if (_iniFromTc == normalized)
                return;

            _iniFromTc = normalized;
            _store = string.Empty; // путь снова надо определить: TC подсказал другой файл
            PluginTrace.Write($"PackSetDefaultParams: ini пакеров «{normalized}»");
        }
    }

    public sealed record Settings(int Level, int ChunkKiB)
    {
        public int ChunkBytes => ChunkKiB * 1024;
    }

    /// <summary>Файл, куда плагин пишет настройки прямо сейчас (для журнала и строки в диалоге).</summary>
    public static string StorePath
    {
        get
        {
            lock (Gate)
                return ResolveStore();
        }
    }

    /// <summary>Текущие настройки: из ini, а если ключей нет — значения по умолчанию.</summary>
    public static Settings Read()
    {
        lock (Gate)
        {
            string primary = ResolveStore();
            string secondary = FallbackStore();

            int level = ReadInt(LevelKey, primary, secondary, DefaultLevel);
            int chunkKiB = ReadInt(ChunkKey, primary, secondary, DefaultChunkKiB);

            // В ini размер чанка лежит в КиБ, поэтому кратность КиБ обеспечивается самой формой
            // хранения; границы (64 КиБ..16 МиБ) проверяет ReadInt.
            return new Settings(level, chunkKiB);
        }
    }

    /// <summary>То же, что Read, но уже для ядра: PackingOptions для создаваемого контейнера.</summary>
    public static PackingOptions CurrentOptions()
    {
        Settings settings = Read();
        return new PackingOptions(settings.ChunkBytes, settings.Level);
    }

    /// <summary>Сохранить выбор пользователя. Возвращает false, если ни один ini не позволил писать.</summary>
    public static bool Save(int level, int chunkKiB, out string storePath)
    {
        lock (Gate)
        {
            string levelText = level.ToString(CultureInfo.InvariantCulture);
            string chunkText = chunkKiB.ToString(CultureInfo.InvariantCulture);

            string path = ResolveStore();
            if (WriteValues(path, levelText, chunkText))
            {
                storePath = path;
                return true;
            }

            string fallback = FallbackStore();
            if (fallback != path && WriteValues(fallback, levelText, chunkText))
            {
                _store = fallback;
                storePath = fallback;
                PluginTrace.Write($"Настройки: в «{path}» записать не вышло, сохраняю в «{fallback}»");
                return true;
            }

            storePath = path;
            PluginTrace.Write($"Настройки: записать не удалось ни в «{path}», ни в «{fallback}»");
            return false;
        }
    }

    // ─────────────────────────── ini ───────────────────────────

    /// <summary>Определяет файл настроек. Вызывать только под Gate.</summary>
    private static string ResolveStore()
    {
        if (_store.Length != 0)
            return _store;

        _store = _iniFromTc is { Length: > 0 } ? _iniFromTc : FallbackStore();
        return _store;
    }

    private static string FallbackStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ContainerCC");
        return Path.Combine(directory, "ContainerCC.ini");
    }

    /// <summary>Читает целое из секции [ContainerCC]: подсказанный TC файл, затем файл плагина.</summary>
    private static int ReadInt(string key, string primary, string secondary, int defaultValue)
    {
        if (TryReadInt(key, primary, out int value) ||
            (secondary != primary && TryReadInt(key, secondary, out value)))
        {
            int min = key == LevelKey ? MinLevel : MinChunkKiB;
            int max = key == LevelKey ? MaxLevel : MaxChunkKiB;

            if (value < min || value > max)
            {
                PluginTrace.Write($"Настройки: «{key}» = {value} вне диапазона {min}..{max} — беру {defaultValue}");
                return defaultValue;
            }

            return value;
        }

        return defaultValue;
    }

    private static unsafe bool TryReadInt(string key, string path, out int value)
    {
        value = 0;
        if (path.Length == 0 || !File.Exists(path))
            return false;

        Span<char> buffer = stackalloc char[64];
        fixed (char* sectionPtr = Section)
        fixed (char* keyPtr = key)
        fixed (char* defaultPtr = Unset)
        fixed (char* pathPtr = path)
        fixed (char* bufferPtr = buffer)
        {
            uint copied = GetPrivateProfileStringW(sectionPtr, keyPtr, defaultPtr, bufferPtr, buffer.Length, pathPtr);
            string text = new string(bufferPtr, 0, (int)copied);

            if (text.Length == 0 || text == Unset)
                return false;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                PluginTrace.Write($"Настройки: «{key}» = «{text}» — не целое число");
                value = 0;
                return false;
            }

            return true;
        }
    }

    private static unsafe bool WriteValues(string path, string levelText, string chunkText)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 } && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"Настройки: каталог для «{path}» создать не удалось: {ex.Message}");
            return false;
        }

        fixed (char* sectionPtr = Section)
        fixed (char* levelKeyPtr = LevelKey)
        fixed (char* chunkKeyPtr = ChunkKey)
        fixed (char* levelTextPtr = levelText)
        fixed (char* chunkTextPtr = chunkText)
        fixed (char* pathPtr = path)
        {
            bool levelOk = WritePrivateProfileStringW(sectionPtr, levelKeyPtr, levelTextPtr, pathPtr) != 0;
            bool chunkOk = WritePrivateProfileStringW(sectionPtr, chunkKeyPtr, chunkTextPtr, pathPtr) != 0;
            return levelOk && chunkOk;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe uint GetPrivateProfileStringW(
        char* section, char* key, char* defaultValue, char* buffer, int bufferSize, char* filePath);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int WritePrivateProfileStringW(
        char* section, char* key, char* value, char* filePath);
}
