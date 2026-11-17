using System.Buffers.Binary;
using System.IO.Hashing;

namespace Container_V3.Packing;

/// <summary>Порядок обхода чанков: всегда по возрастанию номера — иначе порядок записи рассинхронизируется с индексом.</summary>
public static class ChunkOrder
{
    public static List<List<ChunkFragment>> GroupByChunk(PackingPlan plan)
    {
        var groups = new List<List<ChunkFragment>>(plan.ChunkCount);
        for (int i = 0; i < plan.ChunkCount; i++)
            groups.Add([]);

        // Fragments уже идут в порядке записи, но не полагаемся на это: сортировка дешёвая и делает порядок явным.
        foreach (var f in plan.Fragments)
        {
            if (f.ChunkIndex < 0 || f.ChunkIndex >= plan.ChunkCount)
                throw new ContainerFormatException($"Фрагмент ссылается на чанк #{f.ChunkIndex} вне диапазона 0..{plan.ChunkCount - 1}.");
            groups[f.ChunkIndex].Add(f);
        }

        foreach (var g in groups)
            g.Sort(static (a, b) => a.OffsetInChunk.CompareTo(b.OffsetInChunk));

        return groups;
    }
}
