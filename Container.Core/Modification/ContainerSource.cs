namespace Container_V3.Modification;

/// <summary>
/// Что добавляем: файл на диске и имя, под которым он ляжет в контейнер.
/// Имя задаёт вызывающий (Total Commander или CLI), а не сканер: панель передаёт список
/// «SrcPath + AddList» и подпуть SubPath, и именно из них складывается путь внутри контейнера.
/// </summary>
public sealed record ContainerSource(string SourceFullPath, string RelativePath);

/// <summary>
/// Список «источник → имя в контейнере» превращает в записи, готовые к упаковке.
/// </summary>
public static class ContainerSources
{
    /// <summary>
    /// Имена для режима командной строки: файл берётся под своим именем, папка — под своим
    /// её содержимое — относительными путями внутри неё (ровно как у pack).
    /// </summary>
    public static List<ContainerSource> FromPlainPaths(IReadOnlyList<string> sourcePaths)
    {
        var list = new List<ContainerSource>(sourcePaths.Count);
        foreach (string path in sourcePaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string full = Path.GetFullPath(path);
            string name = Path.GetFileName(full.TrimEnd('\\', '/'));

            if (name.Length == 0)
                throw new ContainerFormatException($"Не из чего получить имя внутри контейнера: '{path}'.");

            list.Add(new ContainerSource(full, name));
        }
        return list;
    }

    public static List<SourceEntry> Expand(IReadOnlyList<ContainerSource> sources, ISourceScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        var result = new List<SourceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ContainerSource source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceFullPath))
                throw new ContainerFormatException("Путь источника пуст.");

            string relative = ContainerPath.Normalize(source.RelativePath);
            string full = Path.GetFullPath(source.SourceFullPath);

            if (Directory.Exists(full))
            {
                bool any = false;
                foreach (SourceEntry entry in scanner.Scan(full))
                {
                    string childPath = relative + "\\" + ContainerPath.Normalize(entry.RelativePath);

                    // Панель может передать папку целиком и отдельно — файл из неё. Одно имя один раз.
                    if (!seen.Add(childPath))
                        continue;

                    any = true;
                    result.Add(entry with { RelativePath = childPath });
                }

                // Пустая папка сканером не отдаёт ни одной записи, а смысл добавления именно в ней:
                // структура дерева. Поэтому пустой каталог попадает в индекс явно.
                if (!any && seen.Add(relative))
                    result.Add(new SourceEntry(full, relative, 0L, true));
            }
            else if (File.Exists(full))
            {
                if (!seen.Add(relative))
                    continue;

                result.Add(new SourceEntry(full, relative, new FileInfo(full).Length, false));
            }
            else
            {
                throw new DirectoryNotFoundException($"Источник не найден: {source.SourceFullPath}");
            }
        }

        return result;
    }
}
