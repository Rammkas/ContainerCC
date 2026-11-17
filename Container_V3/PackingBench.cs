using System.Diagnostics;
using Container_V3.FileSystem;
using Container_V3.IO;
using Container_V3.Packing;

namespace Container_V3;

/// <summary>
/// Измерение наполнения чанка: V2-стиль (открытие файла на каждый фрагмент + временный массив)
/// против FileReadPool (один файл на чанк, запись прямо в буфер чанка).
///
/// Зачем этот код существует. Оптимизацию нельзя принимать на слово: ожидание по чтению кода V2
/// оказалось хуже реальности, и видно это только по замеру. Замер сделан внутри одного процесса,
/// с прогревом перед обоими вариантами, чтобы результат не зависел от JIT и от порядка вариантов.
/// Дополнительно сверяются байты: сравнивать скорость разных алгоритмов бессмысленно, если они
/// читают не одно и то же.
///
/// Запуск: pack-bench &lt;папка&gt; [папка2 ...]
/// </summary>
public static class PackingBench
{
    private const int ChunkSize = ContainerFormat.DefaultChunkSize;
    private const int Rounds = 5;

    public static int Run(string[] dirs)
    {
        if (dirs.Length == 0)
        {
            Console.WriteLine("usage: pack-bench <folder> [folder2 ...]");
            return 1;
        }

        foreach (string dir in dirs)
            Measure(dir);
        return 0;
    }

    private static void Measure(string dir)
    {
        var entries = new List<SourceEntry>(new SourceScanner().Scan(dir));
        if (entries.Count == 0)
        {
            Console.WriteLine($"нет файлов: {dir}");
            return;
        }

        PackingPlan plan = ChunkPlanner.Create(entries, ChunkSize);
        List<List<ChunkFragment>> groups = ChunkOrder.GroupByChunk(plan);

        long fragments = 0;
        foreach (List<ChunkFragment> g in groups)
            fragments += g.Count;

        long totalBytes = 0;
        foreach (SourceEntry e in entries)
            totalBytes += e.Size;

        Console.WriteLine();
        Console.WriteLine($"=== {dir}");
        Console.WriteLine($"файлов {entries.Count}, чанков {groups.Count}, фрагментов {fragments}, данных {totalBytes:N0} байт");

        // Прогрев одинаковый для обоих вариантов: и JIT, и кэш ОС.
        for (int r = 0; r < 2; r++)
            FillWithPool(groups);

        double v2 = Best(FillV2Style, groups);
        double pool = Best(FillWithPool, groups);

        Console.WriteLine($"V2-стиль   (open на фрагмент + временный массив) : {v2:F3} с");
        Console.WriteLine($"FileReadPool (open на чанк, запись в буфер чанка) : {pool:F3} с");

        if (pool <= v2)
            Console.WriteLine($"разница: быстрее на {(v2 / pool - 1) * 100:F1}%");
        else
            Console.WriteLine($"разница: медленнее на {(pool / v2 - 1) * 100:F1}% — переиспользование дескрипторов здесь не окупается");

        uint a = Checksum(groups, FillV2Style);
        uint b = Checksum(groups, FillWithPool);
        Console.WriteLine(a == b
            ? $"байты совпадают ({a:X8}): оба варианта читают одно и то же."
            : $"РАСХОЖДЕНИЕ контрольных сумм: {a:X8} против {b:X8}.");
    }

    private static double Best(Action<List<List<ChunkFragment>>> work, List<List<ChunkFragment>> groups)
    {
        work(groups);

        double best = double.MaxValue;
        for (int r = 0; r < Rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            work(groups);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }
        return best;
    }

    /// <summary>Как в V2: новый FileStream на каждый фрагмент, чтение во временный массив, затем копирование в чанк.</summary>
    private static void FillV2Style(List<List<ChunkFragment>> groups)
    {
        foreach (List<ChunkFragment> group in groups)
        {
            int length = ChunkPlanner.UncompressedLengthOf(group, ChunkSize);
            using var buffer = new PooledBuffer(length);

            foreach (ChunkFragment fragment in group)
            {
                byte[] tmp = new byte[fragment.DataLength];
                using (FileStream fs = new(fragment.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(fragment.SourceOffset, SeekOrigin.Begin);
                    fs.ReadExactly(tmp);
                }
                tmp.AsSpan().CopyTo(buffer.Span.Slice(fragment.OffsetInChunk, fragment.DataLength));
            }
        }
    }

    private static void FillWithPool(List<List<ChunkFragment>> groups)
    {
        foreach (List<ChunkFragment> group in groups)
        {
            int length = ChunkPlanner.UncompressedLengthOf(group, ChunkSize);
            using var buffer = new PooledBuffer(length);
            using var files = new FileReadPool();
            ChunkFiller.Fill(group, buffer.Span, files);
        }
    }

    /// <summary>Контрольная сумма по содержимому всех чанков: ловит случай, когда вариант читает не те байты.</summary>
    private static uint Checksum(List<List<ChunkFragment>> groups, Action<List<List<ChunkFragment>>> fill)
    {
        const uint FnvOffset = 2166136261;
        const uint FnvPrime = 16777619;

        uint hash = FnvOffset;
        foreach (List<ChunkFragment> group in groups)
        {
            int length = ChunkPlanner.UncompressedLengthOf(group, ChunkSize);
            using var buffer = new PooledBuffer(length);
            fill([group]);

            Span<byte> span = buffer.Span;
            for (int i = 0; i < span.Length; i++)
                hash = (hash ^ span[i]) * FnvPrime;
        }
        return hash;
    }
}
