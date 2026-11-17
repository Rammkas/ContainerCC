namespace Container_V3;

/// <summary>
/// Числовые границы формата. Одни и те же константы используют CLI, упаковщик и планировщик чанков:
/// раньше лимит 1024 был продублирован в двух местах, и изменение одного из них сделало бы
/// проверку противоречивой.
/// </summary>
public static class ContainerFormat
{
    /// <summary>
    /// Размер чанка по умолчанию — 1 МБ. Замерено на pyfa (5071 файл, 239 МБ), Ryzen 7 5700X;
    /// медиана из 7 прогонов с отброшенными минимумом и максимумом:
    ///
    ///   чанк     контейнер   чанков  полная расп.   eve.db 97 МБ   мелкий файл
    ///    64K       90,22 MB    3831      4,70 с         638 мс        4,76 мс
    ///   256K       82,31 MB     958      4,26 с         190 мс        5,04 мс
    ///  1024K       79,12 MB     240      3,72 с         109 мс        6,16 мс
    ///  2048K       76,86 MB     120      3,67 с          88 мс        5,77 мс
    ///  4096K       75,16 MB      60      4,03 с          82 мс        7,03 мс
    ///
    /// Изначальное обоснование «из-за кэша процессора» замер не подтвердил: при 8 потоках и чанке
    /// 1 МБ живых данных (сжатый блок + распакованный) выходит ~10,9 МБ — треть L3 в 32 МБ.
    /// Кэш начинает давить только у 4 МБ (~43 МБ, 1,3 xL3), и там деградируют сразу все метрики.
    ///
    /// Реальные границы другие. Снизу (64K) контейнер на 14% больше и выборочная распаковка
    /// большого файла дороже в 6 раз: он лежит в 1525 чанках вместо 96. Сверху (4M) экономия
    /// объёма всего 5%, а ради одного мелкого файла приходится читать и распаковывать целых 4 МБ.
    ///
    /// 2 МБ — следующий кандидат и он неочевидный: на pyfa он выигрывает у 1 МБ по объёму (-2,9%),
    /// по полной распаковке (-1,4%) и по большому файлу (-19%), но проигрывает по одному мелкому
    /// файлу (+6%). На корпусе «один файл 200 МБ» разница в пределах шума. То есть выбор между
    /// 1M и 2M зависит от того, что чаще распаковывают: много мелких файлов (1M) или крупные
    /// файлы целиком (2M). Для контейнера общего назначения оставлен 1M.
    /// </summary>
    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>
    /// Нижняя граница. Меньше — проигрыш по объёму (сжатие мелких блоков хуже) и резкий рост числа
    /// чанков, а значит и стоимости выборочной распаковки большого файла.
    /// </summary>
    public const int MinChunkSize = 64 * 1024;

    /// <summary>
    /// Верхняя граница. Выше 4 МБ на этом железе живые данные одного параллельного прохода
    /// перестают помещаться в L3 и деградируют все метрики сразу. 16 МБ оставлено запасом для
    /// машин с большим кэшем; на 8-ядерном Ryzen значения выше 4 МБ выбирать не стоит.
    /// </summary>
    public const int MaxChunkSize = 16 * 1024 * 1024;

    /// <summary>
    /// Уровень zstd по умолчанию — 3. Одно значение для CLI, плагина и PackingOptions: раньше оно
    /// было продублировано в Cli.DefaultCompressionLevel и в записи PackingOptions, и «по умолчанию»
    /// у разных путей сборки могло разъехаться.
    /// </summary>
    public const int DefaultCompressionLevel = 3;
}

/// <summary>Заголовок контейнера. 36 байт, фиксированный (см. ContainerFormat.md).</summary>
public sealed record ContainerHeader(int Version, int ChunkSize, int HashLength, int CompressionLevel, long OffsetToIndex)
{
    public const string Magic = "ZST3";
    public const int CurrentVersion = 3;
    public const int Size = 36;

    /// <summary>Первые 32 байта заголовка — то, по чему считается HeaderCrc32.</summary>
    public const int CrcCoveredBytes = 32;

    /// <summary>Смещение поля OffsetToIndex относительно начала файла (magic 4 + version 4 + chunkSize 4 + hashLen 4 + level 4 = 20).</summary>
    public const int OffsetToIndexField = 20;
}

/// <summary>Запись сканирования: файл или пустая папка на диске.</summary>
public sealed record SourceEntry(string FullPath, string RelativePath, long Size, bool IsDirectory);

/// <summary>Фрагмент файла внутри чанка. OffsetInChunk и BlockSize — по РАСПАКОВАННЫМ данным.</summary>
public sealed record ChunkFragment(string SourcePath, long SourceOffset, int DataLength, int ChunkIndex, int OffsetInChunk);

/// <summary>План упаковки: что куда кладём. Чанки пронумерованы плотно, 0..Chunks-1.</summary>
public sealed class PackingPlan
{
    public required List<ChunkFragment> Fragments { get; init; }
    public required Dictionary<string, List<ChunkFragment>> ByFile { get; init; }
    public required int ChunkCount { get; init; }
    public required int ChunkSize { get; init; }
}

/// <summary>Где чанк лежит в контейнере и сколько он весил до сжатия.</summary>
public sealed record ChunkInfo(int ChunkNumber, long OffsetInContainer, long HashPosition, int UncompressedLength);

/// <summary>Файл в контейнере. Fragments пусты для пустых папок и файлов нулевого размера.</summary>
public sealed record FileEntry(string Path, long Size, bool IsEmptyFolder, bool IsSplitted, List<ChunkFragmentRef> Fragments);

/// <summary>Ссылка файла на кусок чанка.</summary>
public sealed record ChunkFragmentRef(int ChunkNumber, int OffsetInChunk, int BlockSize);

/// <summary>Индекс контейнера после чтения. Счётчики — вычисляемые, как в V2.</summary>
public sealed record ContainerIndex(IReadOnlyList<FileEntry> Files, IReadOnlyList<ChunkInfo> Chunks)
{
    public int FilesCount => Files.Count;
    public int ChunkCount => Chunks.Count;

    /// <summary>Записи пустых папок. Каталоги с содержимым в индексе отдельной записью не живут.</summary>
    public int FolderCount => Files.Count(f => f.IsEmptyFolder);
}

/// <summary>Мутабельный построитель индекса. Отделён от записи на диск намеренно.</summary>
public sealed class IndexBuilder
{
    private readonly List<FileEntry> _files = [];
    private readonly List<ChunkInfo> _chunks = [];

    public void AddFile(FileEntry file) => _files.Add(file);
    public void AddChunk(ChunkInfo chunk) => _chunks.Add(chunk);

    public ContainerIndex Build() => new(_files, _chunks);
}
