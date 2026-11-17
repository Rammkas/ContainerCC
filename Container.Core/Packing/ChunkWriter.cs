namespace Container_V3.Packing;

/// <summary>
/// Наполнение контейнера чанками: сжатие параллельно, запись строго по порядку чанков.
///
/// Шаг общий для упаковки с нуля и для дописывания в существующий контейнер: единственное
/// отличие — firstChunkNumber. Старые чанки занимают номера 0..N-1 и перенумеровать их нельзя,
/// потому что индекс существующих файлов ссылается на них по номеру (RestorePlanner берёт
/// index.Chunks[ChunkNumber]). Поэтому новые чанки получают номера, продолжая список.
/// </summary>
public static class ChunkWriter
{
    public static void Write(
        List<List<ChunkFragment>> groups,
        int chunkSize,
        ContainerWriter writer,
        IndexBuilder chunks,
        ICompressor compressor,
        Func<ICompressor>? compressorFactory,
        IHasher hasher,
        int firstChunkNumber,
        IPackingProgress? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstChunkNumber);

        // Сжатие — параллельно (свои компрессоры: ZstdSharp.Compressor не потокобезопасен),
        // запись — строго по порядку чанков, чтобы номер в индексе совпадал с местом в файле.
        int parallelism = Math.Clamp(Environment.ProcessorCount, 1, 32);

        // Прогресс считает тот же единственный поток, который пишет чанки: сжатие параллельное,
        // но дописываются они всё равно группами по порядку. Поэтому «файл записан целиком» —
        // это момент, когда исчерпаны его фрагменты, а не отдельная учёка где-то сбоку.
        Dictionary<string, FileLeft>? remaining = progress is null ? null : CountFragments(groups);

        for (int start = 0; start < groups.Count; start += parallelism)
        {
            int end = Math.Min(start + parallelism, groups.Count);
            int batch = end - start;

            var encoded = new EncodedChunk[batch];
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(batch, parallelism) };

            Parallel.For(0, batch, options, i =>
            {
                ICompressor local = compressorFactory is null ? compressor : compressorFactory();
                // Пул файлов живёт на время одного чанка: файл, лежащий в нескольких чанках подряд,
                // открывается один раз в чанке, а не один раз на фрагмент.
                using var files = new FileReadPool();
                try
                {
                    encoded[i] = ChunkCodec.Encode(groups[start + i], chunkSize, local, hasher, files);
                }
                finally
                {
                    if (compressorFactory is not null && local is IDisposable disposable)
                        disposable.Dispose();
                }
            });

            for (int i = 0; i < batch; i++)
            {
                EncodedChunk chunk = encoded[i];

                long offset = writer.Position;
                writer.Write(chunk.CompressedData);
                long hashPosition = writer.Position;
                writer.Write(chunk.Hash);

                chunks.AddChunk(new ChunkInfo(firstChunkNumber + start + i, offset, hashPosition, chunk.UncompressedLength));
            }

            if (progress is not null && remaining is not null)
                ReportCompleted(progress, groups, start, end, remaining);
        }
    }

    private static Dictionary<string, FileLeft> CountFragments(List<List<ChunkFragment>> groups)
    {
        var counts = new Dictionary<string, FileLeft>(StringComparer.Ordinal);

        foreach (List<ChunkFragment> group in groups)
        {
            foreach (ChunkFragment fragment in group)
            {
                if (!counts.TryGetValue(fragment.SourcePath, out FileLeft? file))
                {
                    file = new FileLeft();
                    counts[fragment.SourcePath] = file;
                }

                file.FragmentsLeft++;
                file.Bytes += fragment.DataLength;
            }
        }

        return counts;
    }

    private static void ReportCompleted(
        IPackingProgress progress,
        List<List<ChunkFragment>> groups,
        int start,
        int end,
        Dictionary<string, FileLeft> remaining)
    {
        for (int i = start; i < end; i++)
        {
            foreach (ChunkFragment fragment in groups[i])
            {
                if (!remaining.TryGetValue(fragment.SourcePath, out FileLeft? file))
                    continue; // файл уже отмечен: его фрагменты закончились в более раннем чанке

                file.FragmentsLeft--;
                if (file.FragmentsLeft > 0)
                    continue;

                remaining.Remove(fragment.SourcePath);

                if (!progress.FilePacked(fragment.SourcePath, file.Bytes))
                    throw new PackingAbortedException($"Упаковка прервана на файле '{fragment.SourcePath}'.");
            }
        }
    }

    private sealed class FileLeft
    {
        public int FragmentsLeft;
        public long Bytes;
    }
}
