namespace Container_V3;

/// <summary>
/// Перевод пути записи контейнера в путь на диске.
///
/// Имена внутри .cc хранятся канонично с обратным слэшем («docs\a.txt», см. ContainerPath):
/// так их показала панель Windows, и так их читают CLI и WCX-плагин. На диске Windows это
/// вложенная папка, но на Android и Linux обратный слэш — обычный символ имени файла:
/// Path.Combine(root, "docs\a.txt") даёт файл «docs\a.txt» в одной папке вместо «docs/a.txt».
/// Поэтому путь собирается из сегментов, а не строкой: на Windows результат ровно прежний,
/// на Android получается настоящее дерево каталогов.
///
/// Проверка «не вышла ли за пределы папки распаковки» переехала сюда. Прежняя версия
/// (absolute.StartsWith(root)) пропускала запись вида «..\outside.txt», если папка распаковки
/// называлась «out», а целевой путь получался «outside...»: теперь требуется разделитель
/// сразу после корня.
/// </summary>
public static class ContainerFileSystem
{
    /// <summary>
    /// Абсолютный путь, по которому надо создать запись entryPath внутри папки destinationRoot.
    /// Ошибки формата (выход за пределы, диск, пустое имя) сообщаются здесь, а не при записи.
    /// </summary>
    public static string ToAbsolutePath(string destinationRoot, string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        string root = Path.GetFullPath(destinationRoot);

        string[] segments = entryPath.Split('/', '\\');
        var parts = new List<string>(segments.Length + 1) { root };
        foreach (string segment in segments)
        {
            // «a\\b» и «./a» — шум одних и тех же панелей, а не отдельная папка.
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                throw new ContainerFormatException($"Путь из индекса выходит за пределы папки распаковки: '{entryPath}'.");
            if (segment.Contains(':'))
                throw new ContainerFormatException($"Путь из индекса содержит диск или двоеточие: '{entryPath}'.");

            parts.Add(segment);
        }

        if (parts.Count == 1)
            throw new ContainerFormatException($"Путь из индекса указывает на саму папку распаковки: '{entryPath}'.");

        string absolute = Path.GetFullPath(Path.Combine(parts.ToArray()));
        if (!IsInside(root, absolute))
            throw new ContainerFormatException($"Путь из индекса выходит за пределы папки распаковки: '{entryPath}'.");

        return absolute;
    }

    /// <summary>absolute лежит внутри root, а не является папкой со сходным именем («out» против «outside»).</summary>
    public static bool IsInside(string root, string absolute)
    {
        string full = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return absolute.Length > full.Length
            && absolute.StartsWith(full, StringComparison.OrdinalIgnoreCase)
            && (absolute[full.Length] == Path.DirectorySeparatorChar || absolute[full.Length] == Path.AltDirectorySeparatorChar);
    }
}
