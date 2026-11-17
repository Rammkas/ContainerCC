namespace Container_V3;

/// <summary>
/// Имя записи внутри контейнера. Одно правило нормализации на весь проект: CLI, упаковщик,
/// модификация контейнера и WCX-плагин обязаны писать и читать одинаково, иначе запись,
/// добавленную одним способом, нельзя будет достать другим.
///
/// В V2 имена правились по месту (SubPath + TrimStart) в четырёх копиях упаковщика, и
/// «a\b» против «\a\b» уже приводило к потере файлов при распаковке.
/// </summary>
public static class ContainerPath
{
    /// <summary>
    /// Предельная длина пути записи. Поле FileName в tHeaderDataExW — 1024 символа: имя длиннее
    /// Total Commander покажет обрезанным, и копирование «из контейнера» по такому имени уже не работает.
    /// </summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// Привести имя к каноничному виду: обратные слеши, без «.» и без завершающего разделителя.
    /// Выход за пределы контейнера («..»), диск («C:») и символы, которые нельзя положить в имя
    /// файла Windows, отклоняются здесь, а не при распаковке.
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ContainerFormatException("Имя внутри контейнера пустое.");

        string[] segments = raw.Split('/', '\\');
        var clean = new List<string>(segments.Length);
        int length = 0;

        foreach (string segment in segments)
        {
            // «a\\b», «a/» и «./a» — разный шум одних и тех же панелей, а не разные записи.
            if (segment.Length == 0 || segment == ".")
                continue;

            if (segment == "..")
                throw new ContainerFormatException($"Имя выходит за пределы контейнера: '{raw}'.");
            if (segment.Contains(':'))
                throw new ContainerFormatException($"Имя содержит диск или двоеточие: '{raw}'.");
            if (IndexOfInvalidChar(segment) >= 0)
                throw new ContainerFormatException($"Имя содержит недопустимый символ: '{raw}'.");

            length += segment.Length + (clean.Count > 0 ? 1 : 0);
            clean.Add(segment);
        }

        if (clean.Count == 0)
            throw new ContainerFormatException($"Имя без файла: '{raw}'.");
        if (length > MaxLength)
            throw new ContainerFormatException($"Имя длиннее {MaxLength} символов: '{raw}'.");

        return string.Join('\\', clean);
    }

    private static int IndexOfInvalidChar(string segment) => IndexOfInvalidChar(segment, wildcardsAllowed: false);

    /// <summary>
    /// Символ, который нельзя положить в имя записи. Для маски (RemovalPattern) «*» и «?» —
    /// подстановка, а не часть имени, поэтому там они разрешены.
    /// </summary>
    public static int IndexOfInvalidChar(string segment, bool wildcardsAllowed)
    {
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (c < ' ' || c is '<' or '>' or '"' or '|')
                return i;
            if (!wildcardsAllowed && c is '?' or '*')
                return i;
        }
        return -1;
    }

    /// <summary>Каталоги-предки: «a\b\c.bin» даёт «a» и «a\b».</summary>
    public static IEnumerable<string> Ancestors(string normalizedPath)
    {
        int index = normalizedPath.IndexOf('\\');
        while (index > 0)
        {
            yield return normalizedPath[..index];
            index = normalizedPath.IndexOf('\\', index + 1);
        }
    }

    /// <summary>
    /// Каталог folder — предок path. Сравнение регистронезависимое, как в Windows: «Docs\a.txt»
    /// удаляется вместе с «docs».
    /// </summary>
    public static bool IsAncestor(string folder, string path) =>
        path.Length > folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && path[folder.Length] == '\\';

    /// <summary>Одно и то же имя в разных регистрах — одна и та же запись (Windows так и видит).</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
