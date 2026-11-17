using System.IO.Hashing;
using Container_V3.FileSystem;

namespace Container_V3.Packing;

/// <summary>Параллельность упаковки: сжатие в пуле потоков, запись строго по порядку чанков.</summary>
public sealed record PackingOptions(int ChunkSize = ContainerFormat.DefaultChunkSize, int CompressionLevel = ContainerFormat.DefaultCompressionLevel)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ChunkSize, ContainerFormat.MinChunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ChunkSize, ContainerFormat.MaxChunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CompressionLevel);
    }
}

/// <summary>
/// Упаковка. Единственный класс, который пишет контейнер; ничего не знает о распаковке.
/// В V2 у него было 7 параметров конструктора и две копии цикла упаковки (PackAllSF / PackAllMT),
/// которые уже разъехались по поведению. Здесь один цикл, а параллельность — свойство пула компрессоров.
/// </summary>
public sealed class ContainerPacker : IDisposable
{
    private readonly ICompressor _compressor;
    private readonly IHasher _hasher;
    private readonly ISourceScanner _scanner;
    private readonly PackingOptions _options;
    private readonly Func<ICompressor>? _compressorFactory;

    /// <summary>
    /// compressorFactory нужен потому, что Compressor из ZstdSharp не потокобезопасен:
    /// V2 создавал новый ZstdCompressor на каждый чанк в MT-ветке и держал ThreadLocal&lt;Compressor&gt;.
    /// </summary>
    public ContainerPacker(ICompressor compressor, IHasher hasher, ISourceScanner scanner, PackingOptions options, Func<ICompressor>? compressorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(scanner);
        options.Validate();

        _compressor = compressor;
        _hasher = hasher;
        _scanner = scanner;
        _options = options;
        _compressorFactory = compressorFactory;
    }

    public void Pack(string containerPath, IReadOnlyList<string> sourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        if (sourcePaths.Count == 0)
            throw new ArgumentException("Список источников пуст.", nameof(sourcePaths));

        var entries = ScanAll(sourcePaths);

        // Проверка исходников до записи: иначе битый файл уедет в контейнер как «целый».
        SourceManifest.Verify(entries);

        var plan = ChunkPlanner.Create(entries, _options.ChunkSize);

        using FileStream fs = new(containerPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        using var writer = new ContainerWriter(fs);

        writer.WriteHeader(_options.ChunkSize, _hasher.Length, _options.CompressionLevel);

        var builder = new IndexBuilder();
        AddNonDataEntries(entries, builder);

        var groups = ChunkOrder.GroupByChunk(plan);

        WriteChunks(groups, writer, builder);
        AddFilesToIndex(plan, entries, builder);
        WriteIndex(writer, builder.Build());
    }

    private List<SourceEntry> ScanAll(IReadOnlyList<string> sourcePaths)
    {
        var all = new List<SourceEntry>();
        foreach (string path in sourcePaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new DirectoryNotFoundException($"Путь не найден: {path}");

            foreach (var entry in _scanner.Scan(path))
                all.Add(entry);
        }
        return all;
    }

    /// <summary>Пустые папки и файлы нулевого размера живут только в индексе — чанка для них нет.</summary>
    private static void AddNonDataEntries(IReadOnlyList<SourceEntry> entries, IndexBuilder builder)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || entry.Size == 0)
                builder.AddFile(new FileEntry(entry.RelativePath, 0, entry.IsDirectory, false, []));
        }
    }

    /// <summary>
    /// Наполнение чанками. Отдельного цикла здесь больше нет: тот же шаг делает дописывание
    /// в существующий контейнер (см. ChunkWriter), difference — только первый номер чанка.
    /// </summary>
    private void WriteChunks(List<List<ChunkFragment>> groups, ContainerWriter writer, IndexBuilder builder) =>
        ChunkWriter.Write(groups, _options.ChunkSize, writer, builder, _compressor, _compressorFactory, _hasher, firstChunkNumber: 0);

    private static void AddFilesToIndex(PackingPlan plan, IReadOnlyList<SourceEntry> entries, IndexBuilder builder)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || entry.Size == 0)
                continue;

            var fragments = plan.ByFile[entry.FullPath];
            var refs = new List<ChunkFragmentRef>(fragments.Count);
            foreach (var f in fragments)
                refs.Add(new ChunkFragmentRef(f.ChunkIndex, f.OffsetInChunk, f.DataLength));

            builder.AddFile(new FileEntry(entry.RelativePath, entry.Size, false, fragments.Count > 1, refs));
        }
    }

    /// <summary>Индекс и правка заголовка — общий шаг с изменением готового контейнера (IndexWriter).</summary>
    private void WriteIndex(ContainerWriter writer, ContainerIndex index) =>
        IndexWriter.Write(writer, index, _compressor, _options.ChunkSize, _hasher.Length, _options.CompressionLevel);

    public void Dispose() => _compressor.Dispose();
}
