namespace Container_V3.FileSystem;

/// <summary>
/// Обход источника. Одна реализация вместо двух (FileScanner в FileSystem\ и GetFlatFileList внутри ContainerV2).
/// Относительный путь считается от самого корня, а не от directoryInfo.Parent — в V2 именно это давало
/// пути вида "Container_01\Models.cs", из-за чего распаковка не могла восстановить исходную структуру.
/// </summary>
public sealed class SourceScanner : ISourceScanner
{
    public IEnumerable<SourceEntry> Scan(string rootPath)
    {
        if (File.Exists(rootPath))
        {
            yield return new SourceEntry(rootPath, Path.GetFileName(rootPath), new FileInfo(rootPath).Length, false);
            yield break;
        }

        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Путь не найден: {rootPath}");

        string root = Path.GetFullPath(rootPath);

        // Скрытые и системные записи включаются намеренно. EnumerateFileSystemEntries по умолчанию их
        // пропускает, из-за чего контейнер молча терял .git (2619 файлов), .vs, Thumbs.db — распаковка
        // возвращала дерево без части содержимого, а проверка «совпало ли всё» на этом не ловилась,
        // потому что сравнивала видимые файлы с видимыми. repo3: 20027 видимых против 20921 реальных.
        // Пропускать можно только то, что сам контейнер пишет рядом с источником; это решает вызывающий код.
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        }))
        {
            if ((GetAttributesSafe(entry) & (FileAttributes.Directory)) != 0)
                continue; // папки добавим отдельно — только пустые нам не нужны вообще

            string full = Path.GetFullPath(entry);
            yield return new SourceEntry(full, Path.GetRelativePath(root, full), new FileInfo(full).Length, false);
        }

        // Пустые папки: без них структура дерева после распаковки потеряется.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        }))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                yield return new SourceEntry(Path.GetFullPath(dir), Path.GetRelativePath(root, dir), 0, true);
        }
    }

    private static FileAttributes GetAttributesSafe(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (IOException)
        {
            // Запись исчезла во время обхода или к ней нет доступа — пропускаем как пропускал бы IgnoreInaccessible.
            return FileAttributes.Directory;
        }
    }
}
