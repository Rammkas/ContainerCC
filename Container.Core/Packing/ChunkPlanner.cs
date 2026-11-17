namespace Container_V3.Packing;

/// <summary>
/// Математика распределения файлов по чанкам. Ничего не читает и ничего не пишет — только план.
/// В проекте было три копии этого алгоритма (ChunkingEngine, ChunkingEngineV2 + два FillChunkWithDataSync).
/// Здесь одна.
/// </summary>
public static class ChunkPlanner
{
    public static PackingPlan Create(IReadOnlyList<SourceEntry> entries, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, ContainerFormat.MinChunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, ContainerFormat.MaxChunkSize);

        var files = new List<SourceEntry>();
        foreach (var e in entries)
            if (!e.IsDirectory && e.Size > 0)
                files.Add(e);

        var fragments = new List<ChunkFragment>();
        var byFile = new Dictionary<string, List<ChunkFragment>>(files.Count);

        int chunkIndex = 0;
        int remainingInChunk = chunkSize;

        foreach (var file in files)
        {
            var own = new List<ChunkFragment>(2);
            byFile[file.FullPath] = own;

            long done = 0;
            while (done < file.Size)
            {
                int take = (int)Math.Min(file.Size - done, remainingInChunk);

                var fragment = new ChunkFragment(file.FullPath, done, take, chunkIndex, chunkSize - remainingInChunk);
                fragments.Add(fragment);
                own.Add(fragment);

                done += take;
                remainingInChunk -= take;

                if (remainingInChunk == 0)
                {
                    chunkIndex++;
                    remainingInChunk = chunkSize;
                }
            }
        }

        // Чанк с partially-filled хвостом тоже существует: его длина меньше chunkSize.
        // В V2 об этом не помнили и сжимали весь арендованный буфер (ChunkV2.cs:162, :372).
        int chunkCount = remainingInChunk == chunkSize ? chunkIndex : chunkIndex + 1;

        return new PackingPlan
        {
            Fragments = fragments,
            ByFile = byFile,
            ChunkCount = chunkCount,
            ChunkSize = chunkSize
        };
    }

    /// <summary>Сколько байт данных реально попадёт в чанк (для хвостового чанка меньше chunkSize).</summary>
    public static int UncompressedLengthOf(List<ChunkFragment> fragmentsInChunk, int chunkSize)
    {
        int maxEnd = 0;
        foreach (var f in fragmentsInChunk)
            maxEnd = Math.Max(maxEnd, f.OffsetInChunk + f.DataLength);

        if (maxEnd > chunkSize)
            throw new ContainerFormatException($"План некорректен: чанк требует {maxEnd} байт при лимите {chunkSize}.");

        return maxEnd;
    }
}
