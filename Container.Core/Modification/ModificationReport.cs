namespace Container_V3.Modification;

/// <summary>
/// Что именно изменилось в контейнере и сколько в нём осталось мусора.
/// </summary>
public sealed record ModificationReport(
    string ContainerPath,
    int AddedEntries,
    int ReplacedEntries,
    int RemovedEntries,
    // сколько каталогов остались в контейнере пустыми после удаления их последнего содержимого
    int PreservedEmptyFolders,
    int AddedChunks,
    long BytesWritten,
    long ContainerSize,
    ContainerHealth Health,
    string? BackupPath)
{
    /// <summary>Изменилось ли что-то фактически.</summary>
    public bool Changed => AddedEntries != 0 || ReplacedEntries != 0 || RemovedEntries != 0;
}

/// <summary>
/// Результат уплотнения: оно всегда переписывает файл целиком, поэтому результат измеряют
/// в сэкономленных байтах, а не в «добавлено/удалено».
/// </summary>
public sealed record CompactReport(
    string ContainerPath,
    long SizeBefore,
    long SizeAfter,
    int DroppedChunks,
    int LiveChunks,
    bool Applied,
    string? BackupPath)
{
    public long Reclaimed => Math.Max(0, SizeBefore - SizeAfter);
}

/// <summary>
/// Состояние контейнера как файла: сколько байт несут данные, сколько — вытесненные индексы
/// и удалённые файлы.
///
/// Добавление и удаление меняют только индекс, поэтому файл растёт и после удаления:
/// мёртвые чанки и старые индексы остаются до compact. Это не ошибка, а цена того, что
/// контейнер на 10 ГБ не переупаковывается целиком; но молчать о ней нельзя — иначе
/// пользователь видит «удалил 3 ГБ, файл стал больше».
/// </summary>
public sealed record ContainerHealth(
    long FileSize,
    int FileCount,
    int FolderCount,
    int ChunkCount,
    int LiveChunkCount,
    int DeadChunkCount,
    long LiveChunkBytes,
    long DeadChunkBytes,
    long IndexBytes,
    long GarbageBytes,
    long EstimatedSizeAfterCompact,
    long ReclaimableBytes)
{
    public double GarbagePercent => FileSize <= 0 ? 0 : (double)ReclaimableBytes / FileSize * 100.0;

    public bool NeedsCompact => ReclaimableBytes > 0;

    /// <summary>
    /// Посчитать состояние по уже известному индексу. Используется и командой info,
    /// и сразу после изменения контейнера — из того же индекса, который только что записали.
    /// </summary>
    public static ContainerHealth Measure(long fileSize, long offsetToIndex, int hashLength, ContainerIndex index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetToIndex);

        var referenced = new HashSet<int>();
        int folders = 0;

        foreach (FileEntry file in index.Files)
        {
            if (file.IsEmptyFolder) folders++;
            foreach (ChunkFragmentRef fragment in file.Fragments)
                referenced.Add(fragment.ChunkNumber);
        }

        long live = 0, dead = 0;
        int liveChunks = 0;

        foreach (ChunkInfo chunk in index.Chunks)
        {
            // Область чанка — сжатые данные плюс хэш этих данных.
            long size = chunk.HashPosition - chunk.OffsetInContainer + hashLength;

            if (referenced.Contains(chunk.ChunkNumber))
            {
                live += size;
                liveChunks++;
            }
            else
            {
                dead += size;
            }
        }

        long indexBytes = Math.Max(0, fileSize - offsetToIndex);
        long dataRegion = Math.Max(0, offsetToIndex - ContainerHeader.Size);

        // Всё, что не чанк и не живой индекс: вытеснённые индексы предыдущих операций и разрывы между чанками.
        long garbage = Math.Max(0, dataRegion - (live + dead));

        long estimate = ContainerHeader.Size + live + indexBytes;
        long reclaimable = Math.Max(0, fileSize - estimate);

        return new ContainerHealth(
            fileSize,
            index.Files.Count,
            folders,
            index.Chunks.Count,
            liveChunks,
            index.Chunks.Count - liveChunks,
            live,
            dead,
            indexBytes,
            garbage,
            estimate,
            reclaimable);
    }

    /// <summary>Прочитать контейнер и посчитать его состояние, ничего не меняя.</summary>
    public static ContainerHealth Inspect(string containerPath, ICompressor compressor)
    {
        using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.RandomAccess);
        using var reader = new ContainerReader(stream);

        ContainerHeader header = reader.ReadHeader();
        ContainerIndex index = reader.ReadIndex(header, compressor);

        return Measure(stream.Length, header.OffsetToIndex, header.HashLength, index);
    }
}
