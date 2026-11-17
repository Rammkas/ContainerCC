namespace Container_V3.Unpacking;

/// <summary>Метаданные контейнера, прочитанные из заголовка.</summary>
public sealed record ContainerInfo(int Version, int ChunkSize, int HashLength, int CompressionLevel, int FileCount, int FolderCount, int ChunkCount);

/// <summary>
/// Распаковка. Ничего не знает об упаковке — только читает файл и пишет на диск.
/// В V2 здесь было 10 методов, половина из которых была P-вариантами (StoreNSFiles/StoreNSFilesP,
/// ReassembleOneFile/ReassembleOneFileP). Здесь один путь: план → чанки → файлы.
/// </summary>
public sealed class ContainerUnpacker : IDisposable
{
    private readonly string _containerPath;
    private readonly ICompressor _compressor;
    private readonly IHasher _hasher;
    private ContainerIndex? _index;

    public ContainerUnpacker(string containerPath, ICompressor compressor, IHasher hasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentNullException.ThrowIfNull(compressor);
        ArgumentNullException.ThrowIfNull(hasher);

        if (!File.Exists(containerPath))
            throw new FileNotFoundException($"Контейнер не найден: {containerPath}", containerPath);

        _containerPath = containerPath;
        _compressor = compressor;
        _hasher = hasher;
    }

    public ContainerInfo ReadInfo()
    {
        using var reader = new ContainerReader(OpenRead(_containerPath));
        ContainerHeader header = reader.ReadHeader();
        ContainerIndex index = GetIndex(reader);

        return new ContainerInfo(
            header.Version,
            header.ChunkSize,
            header.HashLength,
            header.CompressionLevel,
            index.FilesCount,
            index.FolderCount,
            index.ChunkCount);
    }

    public IReadOnlyList<FileEntry> ReadFileList() => ReadIndex().Files;

    /// <summary>Индекс целиком: файлы и чанки. Кэшируется: повторный вызов файл уже не читает.</summary>
    public ContainerIndex ReadIndex()
    {
        using var reader = new ContainerReader(OpenRead(_containerPath));
        return GetIndex(reader);
    }

    /// <summary>Все файлы контейнера в pathToExtract. Каждый чанк читается ровно один раз.</summary>
    public void UnpackAll(string pathToExtract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToExtract);

        using var reader = new ContainerReader(OpenRead(_containerPath));
        ValidateHasher(reader.ReadHeader());

        ContainerIndex index = GetIndex(reader);

        EmptyStore.Create(pathToExtract, RestorePlanner.BuildEmpty(index));

        var jobs = RestorePlanner.GroupByChunk(RestorePlanner.Build(index, pathToExtract), index);
        Run(jobs);

        VerifySizes(index, pathToExtract);
    }

    /// <summary>Один файл по относительному пути из индекса. Читает только те чанки, которые ему нужны.</summary>
    public void UnpackFile(string relativePath, string pathToExtract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToExtract);

        using var reader = new ContainerReader(OpenRead(_containerPath));
        ValidateHasher(reader.ReadHeader());

        ContainerIndex index = GetIndex(reader);
        FileEntry entry = FindFile(index, relativePath);

        if (entry.Fragments.Count == 0)
        {
            EmptyStore.Create(pathToExtract, [entry]);
            return;
        }

        var targets = RestorePlanner.Build(new ContainerIndex([entry], index.Chunks), pathToExtract);
        Run(RestorePlanner.GroupByChunk(targets, index));

        VerifySize(entry, pathToExtract);
    }

    /// <summary>
    /// Один файл в конкретный целевой путь: имя выбирает вызывающий, а не индекс.
    /// Оболочкам вроде Total Commander «корень + путь из индекса» не подходит — при копировании
    /// они сами решают имя (переименование при конфликте), и плагин обязан записать ровно туда.
    /// </summary>
    public void UnpackFileTo(string relativePath, string absoluteTargetFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteTargetFile);

        string target = Path.GetFullPath(absoluteTargetFile);
        string? root = Path.GetDirectoryName(target);
        string leaf = Path.GetFileName(target);

        if (string.IsNullOrEmpty(root) || leaf.Length == 0)
            throw new ArgumentException($"Целевой путь должен указывать на файл, а не на папку: '{target}'.", nameof(absoluteTargetFile));

        using var reader = new ContainerReader(OpenRead(_containerPath));
        ValidateHasher(reader.ReadHeader());

        ContainerIndex index = GetIndex(reader);
        FileEntry entry = FindFile(index, relativePath);

        // Переименование — подмена пути в копии записи: сборка по фрагментам остаётся общей.
        FileEntry renamed = entry with { Path = leaf };
        ContainerIndex single = new([renamed], index.Chunks);

        if (renamed.Fragments.Count == 0)
        {
            EmptyStore.Create(root, [renamed]);
            return;
        }

        var targets = RestorePlanner.Build(single, root);
        Run(RestorePlanner.GroupByChunk(targets, single));

        VerifySize(renamed, root);
    }

    /// <summary>
    /// Проверка целостности одного файла без записи на диск: хэш каждого нужного чанка и декомпрессия.
    /// Нужна оболочкам с командой «проверить архив» (Total Commander вызывает PK_TEST на каждый файл):
    /// проверка обязана быть дешевле, чем распаковывать файл во временную папку.
    /// </summary>
    public void VerifyFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        using var reader = new ContainerReader(OpenRead(_containerPath));
        ValidateHasher(reader.ReadHeader());

        ContainerIndex index = GetIndex(reader);
        FileEntry entry = FindFile(index, relativePath);

        using var restorer = new ChunkRestorer(reader, _hasher);
        HashSet<int> verified = [];

        foreach (ChunkFragmentRef fragment in entry.Fragments)
        {
            if (fragment.ChunkNumber < 0 || fragment.ChunkNumber >= index.Chunks.Count)
                throw new ContainerFormatException($"Файл '{entry.Path}' ссылается на чанк #{fragment.ChunkNumber}, которого нет в индексе.");

            // Фрагменты одного файла могут лежать в одном чанке — проверять его дважды не нужно.
            if (verified.Add(fragment.ChunkNumber))
                restorer.VerifyChunk(index.Chunks[fragment.ChunkNumber], _compressor);
        }
    }

    /// <summary>Поддерево: все файлы, чей путь начинается с folderPath.</summary>
    public void UnpackFolder(string folderPath, string pathToExtract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToExtract);

        using var reader = new ContainerReader(OpenRead(_containerPath));
        ValidateHasher(reader.ReadHeader());

        ContainerIndex index = GetIndex(reader);

        string normalized = folderPath.TrimEnd('/', '\\');
        var selected = new List<FileEntry>();
        foreach (var file in index.Files)
        {
            if (file.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                file.Path.StartsWith(normalized + '/', StringComparison.OrdinalIgnoreCase) ||
                file.Path.StartsWith(normalized + '\\', StringComparison.OrdinalIgnoreCase))
                selected.Add(file);
        }

        if (selected.Count == 0)
            throw new ContainerFormatException($"В контейнере нет файлов по пути '{folderPath}'.");

        EmptyStore.Create(pathToExtract, RestorePlanner.BuildEmpty(new ContainerIndex(selected, index.Chunks)));

        var targets = RestorePlanner.Build(new ContainerIndex(selected, index.Chunks), pathToExtract);
        Run(RestorePlanner.GroupByChunk(targets, index));

        foreach (var entry in selected)
            VerifySize(entry, pathToExtract);
    }

    private void Run(List<ChunkReadJob> jobs)
    {
        if (jobs.Count == 0)
            return;

        // Один файл собирается из фрагментов нескольких чанков, а разные чанки обрабатываются параллельно.
        // Если бы каждый поток открывал сам целевой файл, два потока спорили бы за него
        // (файл уже открыт другим потоком на запись). Поэтому сборка идёт во временные части:
        // одна часть = один фрагмент, и часть принадлежит ровно одному потоку. Затем целевой файл
        // дописывается строго по номерам чанков одним потоком и только потом закрывается:
        // порядок кусков совпадает с порядком данных в исходном файле.
        string stagingRoot = Path.Combine(Path.GetTempPath(), "container_v3_parts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        var partPerItem = new string[jobs.Count][];
        int totalParts = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var parts = new string[jobs[i].Items.Count];
            for (int j = 0; j < parts.Length; j++, totalParts++)
                parts[j] = Path.Combine(stagingRoot, totalParts.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".part");
            partPerItem[i] = parts;
        }

        // Целевой файл создаётся ровно один раз и дописывается строго по возрастанию номера чанка.
        // Файл держится открытым до своего последнего фрагмента, поэтому один поток пишет в него
        // несколько кусков и никто не пишет в уже закрытый FileStream.
        var openTargets = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

        try
        {
            int parallelism = Math.Clamp(Environment.ProcessorCount, 1, 8); // упор в диск, а не в ядра
            var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };

            // Каждый поток читает свой диапазон файла через отдельный Stream: у одного FileStream общая позиция.
            Parallel.For(0, jobs.Count, options, i =>
            {
                using var reader = new ContainerReader(OpenRead(_containerPath));
                using ICompressor compressor = CreateCompressor();
                using var restorer = new ChunkRestorer(reader, _hasher);
                restorer.Restore(jobs[i], compressor, partPerItem[i]);
            });

            for (int i = 0; i < jobs.Count; i++)
                foreach (var target in DistinctTargets(jobs[i]))
                    AppendToTarget(target, jobs[i], partPerItem[i], openTargets);
        }
        finally
        {
            foreach (var fs in openTargets.Values)
                fs.Dispose();
            try { Directory.Delete(stagingRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static IEnumerable<RestoreTarget> DistinctTargets(ChunkReadJob job)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, _) in job.Items)
            if (seen.Add(target.AbsolutePath))
                yield return target;
    }

    private static void AppendToTarget(RestoreTarget target, ChunkReadJob job, string[] parts, Dictionary<string, FileStream> openTargets)
    {
        if (!openTargets.TryGetValue(target.AbsolutePath, out var fs))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.AbsolutePath)!);
            fs = OpenTarget(target.AbsolutePath);
            openTargets[target.AbsolutePath] = fs;
        }

        for (int i = 0; i < job.Items.Count; i++)
        {
            if (!job.Items[i].Target.AbsolutePath.Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                continue;

            using var part = new FileStream(parts[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            part.CopyTo(fs);
        }
    }

    /// <summary>
    /// Целевой файл создаётся один раз. Если он уже есть на диске — это повторная распаковка в ту же
    /// папку, и прежний файл заменяется новым: распаковка должна быть идемпотентной, иначе выборочная
    /// распаковка второго файла в ту же директорию падала бы с «файл уже существует».
    /// </summary>
    private static FileStream OpenTarget(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            try
            {
                return new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(absolutePath))
            {
                // Файл появился между проверкой и созданием: кто-то пишет сюда параллельно.
                // Молча перезаписывать нельзя — два автора в одном файле дадут смесь данных.
                throw new ReassemblyException(absolutePath, -1, 0,
                    new IOException("Файл появился одновременно с распаковкой; повторите операцию."));
            }
        }

        return new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
    }

    private static FileEntry FindFile(ContainerIndex index, string relativePath)
    {
        foreach (var file in index.Files)
            if (file.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                return file;

        throw new ContainerFormatException($"Файл '{relativePath}' не найден в контейнере.");
    }

    private void ValidateHasher(ContainerHeader header)
    {
        if (header.HashLength != _hasher.Length)
            throw new ContainerFormatException(
                $"Контейнер создан с хэшем {header.HashLength} байт, а текущий hasher даёт {_hasher.Length}. Нужен тот же алгоритм.");
    }

    /// <summary>
    /// FileShare.ReadWrite, а не Read: панель может держать контейнер открытым для чтения
    /// одновременно с тем, как другой процесс что-то в него дописывает. С FileShare.Read
    /// попытка открыть тот же файл на запись получила бы отказ разделения.
    /// </summary>
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.RandomAccess);

    private ContainerIndex GetIndex(ContainerReader reader)
    {
        if (_index is not null) return _index;

        ContainerHeader header = reader.ReadHeader();
        ValidateHasher(header);
        _index = reader.ReadIndex(header, _compressor);
        return _index;
    }

    private ICompressor CreateCompressor() => new Compressors.ZstdCompressor();

    private void VerifySizes(ContainerIndex index, string pathToExtract)
    {
        foreach (var entry in index.Files)
            if (entry.Fragments.Count > 0)
                VerifySize(entry, pathToExtract);
    }

    private static void VerifySize(FileEntry entry, string pathToExtract)
    {
        string absolute = ContainerFileSystem.ToAbsolutePath(pathToExtract, entry.Path);
        long actual = File.Exists(absolute) ? new FileInfo(absolute).Length : -1;
        if (actual != entry.Size)
            throw new ReassemblyException(entry.Path, entry.Size, (int)actual);
    }

    public void Dispose() => _compressor.Dispose();
}
