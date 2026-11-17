using System.IO.Hashing;

namespace Container_V3.Unpacking;

/// <summary>Файл целиком: куда писать и из каких кусков чанков он собирается.</summary>
public sealed record RestoreTarget(string AbsolutePath, FileEntry Entry);

/// <summary>Один шаг распаковки: чанк и все фрагменты файлов, которые из него берутся.</summary>
public sealed record ChunkReadJob(ChunkInfo Chunk, List<(RestoreTarget Target, ChunkFragmentRef Fragment)> Items);

/// <summary>
/// Группировка файлов по чанкам для распаковки.
/// В V2 это делалось дважды по-разному: CreatePlanToOneChunkNSF (для не-split файлов) и отдельный
/// обход SplittedFile — из-за чего появились StoreNSFiles/StoreSFP и их P-варианты. Здесь один план,
/// и файл любой длины собирается одной операцией; при распаковке «всего» каждый чанк читается ровно один раз.
/// </summary>
public static class RestorePlanner
{
    public static List<RestoreTarget> Build(ContainerIndex index, string pathToExtract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToExtract);

        string root = Path.GetFullPath(pathToExtract);
        var targets = new List<RestoreTarget>(index.Files.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in index.Files)
        {
            if (file.Fragments.Count == 0)
                continue; // пустые папки и файлы нулевого размера создаёт отдельный шаг

            // Сегментами, а не строкой: «\» в имени записи на Android — символ имени, а не папка.
            string absolute = ContainerFileSystem.ToAbsolutePath(root, file.Path);

            // Разные записи индекса не должны указывать в один файл на диске: иначе победил бы
            // последний записанный, а первый просто потерялся бы без сообщения.
            if (!claimed.Add(absolute))
                throw new ContainerFormatException(
                    $"Две записи контейнера ведут в один файл на диске: '{file.Path}' → '{absolute}'.");

            targets.Add(new RestoreTarget(absolute, file));
        }

        return targets;
    }

    /// <summary>Файлы без данных (пустые папки и нулевые файлы) — их достаточно создать на диске.</summary>
    public static List<FileEntry> BuildEmpty(ContainerIndex index)
    {
        var empty = new List<FileEntry>();
        foreach (var file in index.Files)
            if (file.Fragments.Count == 0)
                empty.Add(file);
        return empty;
    }

    /// <summary>Сгруппировать фрагменты по чанкам. Каждый чанк читается один раз, все его фрагменты за один проход.</summary>
    public static List<ChunkReadJob> GroupByChunk(List<RestoreTarget> targets, ContainerIndex index)
    {
        var byChunk = new Dictionary<int, ChunkReadJob>();

        foreach (var target in targets)
        {
            foreach (var fragment in target.Entry.Fragments)
            {
                if (fragment.ChunkNumber < 0 || fragment.ChunkNumber >= index.Chunks.Count)
                    throw new ContainerFormatException($"Файл '{target.Entry.Path}' ссылается на чанк #{fragment.ChunkNumber}, которого нет в индексе.");

                if (!byChunk.TryGetValue(fragment.ChunkNumber, out var job))
                {
                    job = new ChunkReadJob(index.Chunks[fragment.ChunkNumber], []);
                    byChunk[fragment.ChunkNumber] = job;
                }

                job.Items.Add((target, fragment));
            }
        }

        var jobs = byChunk.Values.ToList();
        jobs.Sort(static (a, b) => a.Chunk.ChunkNumber.CompareTo(b.Chunk.ChunkNumber));
        return jobs;
    }
}

