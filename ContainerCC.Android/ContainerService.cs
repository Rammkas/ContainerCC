using Container_V3;
using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Modification;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace ContainerCC.Mobile;

/// <summary>
/// UI-слой над тем же ядром формата, что у CLI и WCX-плагина: здесь нет ни одной своей
/// реализации чтения или записи контейнера, только вызовы Container.Core и пересчёт
/// плоского индекса в список «как в папке».
///
/// Отмена. Добавление отменяется по IPackingProgress: возврат false означает «прекращай»,
/// индекс не переписывается, и контейнер остаётся ровно таким, каким был до операции —
/// это поведение самого формата, а не дописка приложения. Извлечение выбранных файлов
/// отменяется между файлами: у распаковки нет точки отчёта внутри файла.
/// «Извлечь всё» идёт одной операцией UnpackAll: так каждый чанк читается один раз даже
/// тогда, когда его фрагменты принадлежат разным файлам, но остановить это нельзя.
/// </summary>
public sealed class ContainerService
{
    /// <summary>Строка списка. Key — путь на устройстве (в режиме файлов) или путь записи внутри контейнера.</summary>
    public sealed record Row(string Name, string Key, long Size, bool IsFolder);

    public delegate void Report(string text);

    private static ContainerModifier OpenModifier(
        int compressionLevel = ContainerFormat.DefaultCompressionLevel,
        int chunkSize = ContainerFormat.DefaultChunkSize) =>
        new(new Sha256Hasher(), new SourceScanner(), new PackingOptions(chunkSize, compressionLevel), level => new ZstdCompressor(level));

    // ---------- просмотр контейнера как папки ----------

    /// <summary>
    /// Что лежит внутри контейнера по пути dir ("" — корень). Индекс плоский («docs\a.txt»),
    /// поэтому папки верхнего уровня выводятся из префиксов записей, а пустые папки берутся
    /// из своих записей — иначе структура дерева после распаковки потерялась бы.
    /// </summary>
    public List<Row> Children(string containerPath, string dir)
    {
        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        return Children(unpacker.ReadFileList(), dir);
    }

    public List<Row> Children(IReadOnlyList<FileEntry> files, string dir)
    {
        string current = dir.TrimEnd('/', '\\');
        var rows = new List<Row>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FileEntry file in files)
        {
            string path = file.Path;

            if (ContainerPath.SamePath(ParentOf(path), current) || (current.Length == 0 && ParentOf(path).Length == 0))
            {
                if (file.IsEmptyFolder)
                    folders.Add(path);   // путь записи пустой папки и есть её полный путь
                else
                    rows.Add(new Row(LeafOf(path), path, file.Size, false));
                continue;
            }

            // Запись лежит глубже: показываем промежуточную папку, а не саму запись.
            if (current.Length == 0 || ContainerPath.IsAncestor(current, path))
            {
                string rest = current.Length == 0 ? path : path[(current.Length + 1)..];
                int cut = rest.IndexOf('\\');
                if (cut > 0)
                    folders.Add(current.Length == 0 ? rest[..cut] : current + '\\' + rest[..cut]);
            }
        }

        var result = new List<Row>(rows.Count + folders.Count);
        foreach (string folder in folders)
            result.Add(new Row(LeafOf(folder) + "/", folder, 0, true));   // показываем лист: Key остаётся полным путём записи

        rows.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.AddRange(rows);
        return result;
    }

    private static string ParentOf(string entryPath)
    {
        int cut = entryPath.LastIndexOf('\\');
        return cut < 0 ? string.Empty : entryPath[..cut];
    }

    private static string LeafOf(string entryPath)
    {
        int cut = entryPath.LastIndexOf('\\');
        return cut < 0 ? entryPath : entryPath[(cut + 1)..];
    }

    // ---------- свойства и проверка ----------

    public (ContainerInfo Info, ContainerHealth Health) Inspect(string containerPath)
    {
        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        return (unpacker.ReadInfo(), ContainerHealth.Inspect(containerPath, new ZstdCompressor()));
    }

    /// <summary>Проверка целостности: хэш каждого нужного чанка и декомпрессия, без записи на диск.</summary>
    public int Verify(string containerPath, IReadOnlyList<string> entryPaths, Report report, Func<bool> shouldStop)
    {
        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        IReadOnlyList<FileEntry> all = unpacker.ReadFileList();

        var selected = new List<FileEntry>();
        foreach (string name in entryPaths)
        {
            FileEntry? found = all.FirstOrDefault(f => ContainerPath.SamePath(f.Path, name));
            if (found is not null)
                selected.Add(found);
        }

        if (selected.Count == 0)
            selected = all.ToList();

        int done = 0;
        foreach (FileEntry entry in selected)
        {
            if (shouldStop())
                throw new OperationCanceledException("Проверка остановлена.");

            if (entry.Fragments.Count > 0)
                unpacker.VerifyFile(entry.Path);

            done++;
            report($"проверено {done} из {selected.Count}: {entry.Path}");
        }

        return done;
    }

    // ---------- извлечение ----------

    /// <summary>Выбранные записи в папку targetDir, с отчётом и отменой между файлами.</summary>
    public int Extract(string containerPath, IReadOnlyList<string> entryPaths, string targetDir, Report report, Func<bool> shouldStop)
    {
        Directory.CreateDirectory(targetDir);

        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        IReadOnlyList<FileEntry> all = unpacker.ReadFileList();

        var selected = ExpandFolders(all, entryPaths);
        int done = 0;

        foreach (FileEntry entry in selected)
        {
            if (shouldStop())
                throw new OperationCanceledException($"Извлечение остановлено после {done} файлов.");

            unpacker.UnpackFile(entry.Path, targetDir);
            done++;
            report($"извлечено {done} из {selected.Count}: {entry.Path}");
        }

        return done;
    }

    /// <summary>Всё содержимое контейнера одной операцией: каждый чанк читается ровно один раз.</summary>
    public void ExtractAll(string containerPath, string targetDir, Report report)
    {
        Directory.CreateDirectory(targetDir);
        report("распаковка контейнера…");

        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        unpacker.UnpackAll(targetDir);
    }

    /// <summary>Сколько займёт на диске извлечение выбранных записей (папка тянет за собой содержимое).</summary>
    public long EstimatedExtractBytes(string containerPath, IReadOnlyList<string> names)
    {
        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        long total = 0;
        foreach (FileEntry entry in ExpandFolders(unpacker.ReadFileList(), names))
            total += entry.Size;
        return total;
    }

    // ---------- изменение ----------

    /// <summary>
    /// Добавить файлы и папки телефона в контейнер. Если контейнера ещё нет — он создастся,
    /// поэтому «упаковать в новый .cc» и «дописать в существующий» — одна и та же операция.
    /// compressionLevel и chunkSize нужны для нового контейнера; в готовом их берёт из его
    /// заголовка сам формат. Отмена по IPackingProgress: контейнер остаётся таким, каким был до начала.
    /// </summary>
    public ModificationReport Append(
        string containerPath,
        IReadOnlyList<string> devicePaths,
        Report report,
        Func<bool> shouldStop,
        int compressionLevel = ContainerFormat.DefaultCompressionLevel,
        int chunkSize = ContainerFormat.DefaultChunkSize)
    {
        var modifier = OpenModifier(compressionLevel, chunkSize);
        var bridge = new ProgressBridge(report, shouldStop, devicePaths.Count);

        try
        {
            return modifier.AppendPaths(containerPath, devicePaths, bridge);
        }
        catch (PackingAbortedException)
        {
            throw new OperationCanceledException($"Добавление остановлено после {bridge.Done} записей; контейнер не изменён, дописанное обрезано.");
        }
    }

    public ModificationReport Remove(string containerPath, IReadOnlyList<string> names) =>
        OpenModifier().Remove(containerPath, names);

    /// <summary>
    /// Сколько записей попадает под маску, не меняя контейнер: чтобы показать «удалится вот это»
    /// до подтверждения. Маска та же, что понимает плагин в Total Commander: «src\*.log» — по
    /// сегментам, «src\*.*» или «src\*» — вся папка src вместе с содержимым.
    /// </summary>
    public (int Total, List<string> Sample) MatchMask(string containerPath, string mask, int limit = 12)
    {
        var pattern = RemovalPattern.Parse(mask);

        using var unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());
        var sample = new List<string>(limit);
        int total = 0;

        foreach (FileEntry file in unpacker.ReadFileList())
        {
            if (!pattern.Matches(file.Path))
                continue;

            total++;
            if (sample.Count < limit)
                sample.Add(file.Path);
        }

        return (total, sample);
    }

    public CompactReport Compact(string containerPath) => OpenModifier().Compact(containerPath);

    /// <summary>Записи + целые папки из выбранных имён: папка тянет за собой всё под ней.</summary>
    private static List<FileEntry> ExpandFolders(IReadOnlyList<FileEntry> all, IReadOnlyList<string> names)
    {
        var picked = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            foreach (FileEntry file in all)
            {
                if (ContainerPath.SamePath(file.Path, name) || ContainerPath.IsAncestor(name, file.Path))
                    if (seen.Add(file.Path))
                        picked.Add(file);
            }
        }

        return picked;
    }

    private sealed class ProgressBridge : IPackingProgress
    {
        private readonly Report _report;
        private readonly Func<bool> _shouldStop;
        private readonly int _total;

        public ProgressBridge(Report report, Func<bool> shouldStop, int total)
        {
            _report = report;
            _shouldStop = shouldStop;
            _total = total;
        }

        public int Done { get; private set; }

        public bool FilePacked(string sourceFullPath, long uncompressedBytes)
        {
            if (_shouldStop())
                return false;

            Done++;
            _report($"добавлено {Done} из {_total}: {Path.GetFileName(sourceFullPath)}");
            return true;
        }
    }
}
