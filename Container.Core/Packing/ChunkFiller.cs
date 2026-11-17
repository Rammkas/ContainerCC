namespace Container_V3.Packing;

/// <summary>
/// Наполнение буфера данными из исходных файлов по плану. Одна реализация вместо трёх копий в V2
/// (ChunkingEngine.FillChunkWithDataSync, ChunkingEngineV2.FillChunkWithDataSync, FillChunkWithDataSync2).
/// </summary>
public static class ChunkFiller
{
    /// <summary>
    /// Заполнить chunkData данными всех фрагментов чанка.
    /// chunkData обязан быть ровно длиной данных чанка: лишнего хвоста арендованного буфера здесь нет.
    /// Файлы открывает files — переиспользуемый пул, живущий на время одного чанка.
    /// </summary>
    public static void Fill(List<ChunkFragment> fragments, Span<byte> chunkData, FileReadPool files)
    {
        foreach (var fragment in fragments)
        {
            // Пишем прямо в буфер чанка: временный массив под фрагмент — это второе прохождение
            // тех же байт через память и лишний garbage.
            files.ReadIntoChunk(fragment.SourcePath, fragment.SourceOffset, chunkData.Slice(fragment.OffsetInChunk, fragment.DataLength));
        }
    }
}
