namespace Container_V3;

/// <summary>
/// Как строка из списка на удаление описывает записи контейнера.
/// </summary>
public enum RemovalScope
{
    /// <summary>Одно имя. Имя файла удаляет файл, имя папки — папку целиком вместе с содержимым.</summary>
    Exact,

    /// <summary>Маска с «*» или «?» внутри сегментов: «src\*.cs» сопоставляется сегмент за сегментом.</summary>
    Mask,

    /// <summary>
    /// Форма «папка\*.*» или «папка\*». Именно так Total Commander передаёт удаление папки из панели:
    /// в DeleteList приходит не имя папки, а маска её содержимого. Удаляется и сама папка, и всё под ней.
    /// </summary>
    Folder,
}

/// <summary>
/// Имя или маска из списка на удаление.
///
/// Total Commander при Del на папке присылает «MauiApp1\*.*», а не «MauiApp1». Прежний код
/// отклонял такие строки (символы «*» и «?» запрещены в имени записи), и удаление папки в панели
/// не работало вовсе. «*» и «?» нельзя положить в контейнер при упаковке — Container.Normalize
/// отклоняет их как недопустимые символы имени. Значит ни одна настоящая запись контейнера не
/// содержит этих символов, и строка с «*» или «?» заведомо является подстановкой, а не именем:
/// маска не может спорить с реальным именем (имя записи проверяет ContainerPath.Normalize).
/// </summary>
public sealed class RemovalPattern
{
    private readonly string[]? _segments;

    private RemovalPattern(string raw, RemovalScope scope, string name, string folder, string[]? segments)
    {
        Raw = raw;
        Scope = scope;
        Name = name;
        Folder = folder;
        _segments = segments;
    }

    /// <summary>Строка дословно, как её прислал вызывающий (для сообщений).</summary>
    public string Raw { get; }

    public RemovalScope Scope { get; }

    /// <summary>Нормализованный текст: для имени — само имя, для маски — маска целиком.</summary>
    public string Name { get; }

    /// <summary>Для Folder — папка, содержимое которой удаляют (пустая строка — весь контейнер).</summary>
    public string Folder { get; }

    public bool IsMask => Scope != RemovalScope.Exact;

    /// <summary>
    /// Разобрать имя или маску. Как и Normalize, отклоняет выход за пределы контейнера («..»),
    /// диск («C:») и управляющие символы, но «*» и «?» здесь означают подстановку.
    /// </summary>
    public static RemovalPattern Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ContainerFormatException("Имя внутри контейнера пустое.");

        string[] parts = raw.Split('/', '\\');
        var segments = new List<string>(parts.Length);
        int length = 0;
        bool mask = false;

        foreach (string part in parts)
        {
            // «a\\b», «a/» и «./a» — разный шум одних и тех же панелей, а не разные записи.
            if (part.Length == 0 || part == ".")
                continue;

            if (part == "..")
                throw new ContainerFormatException($"Имя выходит за пределы контейнера: '{raw}'.");
            if (part.Contains(':'))
                throw new ContainerFormatException($"Имя содержит диск или двоеточие: '{raw}'.");
            if (ContainerPath.IndexOfInvalidChar(part, wildcardsAllowed: true) >= 0)
                throw new ContainerFormatException($"Имя содержит недопустимый символ: '{raw}'.");

            if (part.Contains('*') || part.Contains('?'))
                mask = true;

            length += part.Length + (segments.Count > 0 ? 1 : 0);
            segments.Add(part);
        }

        if (segments.Count == 0)
            throw new ContainerFormatException($"Имя без файла: '{raw}'.");
        if (length > ContainerPath.MaxLength)
            throw new ContainerFormatException($"Имя длиннее {ContainerPath.MaxLength} символов: '{raw}'.");

        string joined = string.Join('\\', segments);

        // «bin\*.*» и «bin\*» — панель просит удалить папку «bin»: её саму и всё, что под ней.
        // «bin\*.log» — наоборот, маска только имён, и поддерево она не вычищает.
        if (mask && IsContentsMask(segments[^1]))
        {
            string folder = segments.Count == 1 ? string.Empty : string.Join('\\', segments[..^1]);
            return new RemovalPattern(raw, RemovalScope.Folder, joined, folder, segments.ToArray());
        }

        return new RemovalPattern(raw, mask ? RemovalScope.Mask : RemovalScope.Exact, joined, string.Empty, segments.ToArray());
    }

    public static List<RemovalPattern> ParseAll(IReadOnlyList<string> raw)
    {
        var patterns = new List<RemovalPattern>(raw.Count);
        foreach (string name in raw)
            patterns.Add(Parse(name));
        return patterns;
    }

    /// <summary>
    /// Попадает ли нормализованное имя записи контейнера под это имя или маску.
    /// normalizedPath должен быть получен ContainerPath.Normalize.
    /// </summary>
    public bool Matches(string normalizedPath) => Scope switch
    {
        // Имя папки убирает её целиком вместе с содержимым — как Del на папке в обычной файловой системе.
        RemovalScope.Exact => ContainerPath.SamePath(normalizedPath, Name)
            || ContainerPath.IsAncestor(Name, normalizedPath),

        // Пустая папка — это корень: маска «*.*» из пустой панели значит «всё, что есть в контейнере».
        RemovalScope.Folder => Folder.Length == 0
            || ContainerPath.SamePath(normalizedPath, Folder)
            || ContainerPath.IsAncestor(Folder, normalizedPath),

        _ => MatchesMask(normalizedPath),
    };

    private bool MatchesMask(string normalizedPath)
    {
        string[] parts = normalizedPath.Split('\\');
        if (parts.Length != _segments!.Length)
            return false;

        for (int i = 0; i < parts.Length; i++)
            if (!SegmentMatches(parts[i], _segments[i]))
                return false;

        return true;
    }

    /// <summary>
    /// Сегмент против сегмента: «*» — любое число символов внутри сегмента (через разделитель он
    /// не перепрыгивает), «?» — ровно один символ. Регистр не важен, как в Windows.
    /// </summary>
    private static bool SegmentMatches(string text, string pattern)
    {
        int t = 0;
        int p = 0;
        int star = -1;
        int starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starText = t;
            }
            else if (star >= 0)
            {
                // Первый вариант «*» не подошёл: расширяем его на один символ и пробуем снова.
                p = star + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    private static bool IsContentsMask(string lastSegment) => lastSegment is "*" or "*.*";

    public override string ToString() => Raw;
}
