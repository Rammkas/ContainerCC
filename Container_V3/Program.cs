using System.Diagnostics;
using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Modification;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace Container_V3;

internal static class Program
{
    private static int Main(string[] args)
    {
        Cli.Options options;
        try
        {
            options = Cli.Parse(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Аргументы разобраны неверно: " + ex.Message);
            PrintUsage();
            return 2;
        }

        try
        {
            return options.Command switch
            {
                "selftest" => SelfTest.Run(),
                "pack" => Pack(options),
                "add" => Add(options),
                "delete" => Delete(options),
                "compact" => Compact(options),
                "info" => Info(options),
                "list" => List(options),
                "unpack-all" => UnpackAll(options),
                "unpack-file" => UnpackFile(options),
                "unpack-folder" => UnpackFolder(options),
                "pack-bench" => PackingBench.Run(options.Positional.ToArray()),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ContainerFormatException or ManifestMismatchException or ReassemblyException)
        {
            Console.WriteLine("ОШИБКА: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Изменение контейнера упирается в файловую систему: файл занят другой программой,
            // нет места, нет прав. Это не ошибка формата, но и не должно выглядеть как крах.
            Console.WriteLine("ОШИБКА файловой системы: " + ex.Message);
            return 1;
        }
    }

    private static int Pack(Cli.Options options)
    {
        if (options.OutputPath is null || options.Positional.Count == 0)
            return Usage();

        var stopwatch = Stopwatch.StartNew();
        using var packer = new ContainerPacker(
            new ZstdCompressor(options.CompressionLevel),
            new Sha256Hasher(),
            new SourceScanner(),
            new PackingOptions(options.ChunkSize, options.CompressionLevel),
            () => new ZstdCompressor(options.CompressionLevel));

        packer.Pack(options.OutputPath, options.Positional);
        stopwatch.Stop();

        long size = new FileInfo(options.OutputPath).Length;
        Console.WriteLine($"Упаковано: {options.OutputPath} ({size:N0} байт) за {stopwatch.Elapsed.TotalSeconds:F1} с");
        return 0;
    }

    private static int Info(Cli.Options options)
    {
        if (options.Positional.Count == 0) return Usage();

        string container = options.Positional[0];

        using var unpacker = Open(container);
        var info = unpacker.ReadInfo();

        Console.WriteLine($"Версия:        {info.Version}");
        Console.WriteLine($"ChunkSize:     {info.ChunkSize}");
        Console.WriteLine($"Длина хэша:    {info.HashLength}");
        Console.WriteLine($"Уровень zstd:  {info.CompressionLevel}");
        Console.WriteLine($"Файлов:        {info.FileCount}");
        Console.WriteLine($"Папок:         {info.FolderCount}");
        Console.WriteLine($"Чанков:        {info.ChunkCount}");

        // Состояние файла как файла: добавление и удаление меняют только индекс, поэтому
        // после удаления контейнер не уменьшается. Это видно здесь и в compact.
        var health = ContainerHealth.Inspect(container, new ZstdCompressor());

        Console.WriteLine($"Размер файла:  {health.FileSize:N0}");
        Console.WriteLine($"Живые чанки:   {health.LiveChunkCount} ({health.LiveChunkBytes:N0} байт)");
        Console.WriteLine($"Мёртвые чанки: {health.DeadChunkCount} ({health.DeadChunkBytes:N0} байт)");
        Console.WriteLine($"Индекс:        {health.IndexBytes:N0}");
        Console.WriteLine($"Прочий мусор:  {health.GarbageBytes:N0}");
        Console.WriteLine(health.NeedsCompact
            ? $"После compact:   {health.EstimatedSizeAfterCompact:N0} (освободится {health.ReclaimableBytes:N0}, {health.GarbagePercent:F1}% файла)"
            : "После compact:   мусора нет");

        return 0;
    }

    private static int Add(Cli.Options options)
    {
        if (options.Positional.Count < 2) return Usage();

        var stopwatch = Stopwatch.StartNew();
        var modifier = OpenModifier(options);

        var report = modifier.AppendPaths(options.Positional[0], options.Positional.Skip(1).ToArray());
        stopwatch.Stop();

        Console.WriteLine(
            $"Добавлено записей: {report.AddedEntries} (заменено {report.ReplacedEntries}), новых чанков: {report.AddedChunks}, " +
            $"записано {report.BytesWritten:N0} байт за {stopwatch.Elapsed.TotalSeconds:F1} с");
        PrintHealth(report);
        return 0;
    }

    private static int Delete(Cli.Options options)
    {
        if (options.Positional.Count < 2) return Usage();

        var stopwatch = Stopwatch.StartNew();
        var modifier = OpenModifier(options);

        var report = modifier.Remove(options.Positional[0], options.Positional.Skip(1).ToArray());
        stopwatch.Stop();

        Console.WriteLine(
            $"Удалено записей: {report.RemovedEntries}, дописано индексов {report.BytesWritten:N0} байт за {stopwatch.Elapsed.TotalSeconds:F1} с. " +
            "Данные на месте: размер файла не уменьшился.");
        PrintHealth(report);
        return 0;
    }

    private static int Compact(Cli.Options options)
    {
        if (options.Positional.Count == 0) return Usage();

        var stopwatch = Stopwatch.StartNew();
        var modifier = OpenModifier(options);

        var report = modifier.Compact(options.Positional[0]);
        stopwatch.Stop();

        if (!report.Applied)
        {
            Console.WriteLine($"Мусора нет: {report.SizeBefore:N0} байт, живых чанков {report.LiveChunks}. Уплотнение не потребовалось.");
            return 0;
        }

        Console.WriteLine(
            $"Уплотнено: {report.SizeBefore:N0} -> {report.SizeAfter:N0} байт (освобождено {report.Reclaimed:N0}), " +
            $"живых чанков {report.LiveChunks}, выброшено {report.DroppedChunks}, за {stopwatch.Elapsed.TotalSeconds:F1} с");
        if (report.BackupPath is not null)
            Console.WriteLine($"Резервная копия: {report.BackupPath}");
        return 0;
    }

    private static void PrintHealth(ModificationReport report)
    {
        var health = report.Health;

        Console.WriteLine($"Размер контейнера: {report.ContainerSize:N0} байт; живых чанков {health.LiveChunkCount}, мёртвых {health.DeadChunkCount}");

        if (health.NeedsCompact)
            Console.WriteLine($"Мусора {health.ReclaimableBytes:N0} байт ({health.GarbagePercent:F1}% файла); compact уменьшит до {health.EstimatedSizeAfterCompact:N0}");
        else
            Console.WriteLine("Мусора нет");

        if (report.BackupPath is not null)
            Console.WriteLine($"Резервная копия: {report.BackupPath}");
    }

    /// <summary>
    /// Изменение контейнера. Уровень сжатия для существующего файла берётся из его заголовка
    /// (об этом заботится сам modifier), поэтому здесь задаются только параметры нового контейнера.
    /// </summary>
    private static ContainerModifier OpenModifier(Cli.Options options) =>
        new(new Sha256Hasher(), new SourceScanner(), new PackingOptions(options.ChunkSize, options.CompressionLevel), level => new ZstdCompressor(level));

    private static int List(Cli.Options options)
    {
        if (options.Positional.Count == 0) return Usage();

        using var unpacker = Open(options.Positional[0]);
        foreach (var file in unpacker.ReadFileList())
        {
            string kind = file.IsEmptyFolder ? "ПАПКА" : "ФАЙЛ ";
            Console.WriteLine($"{kind} {file.Size,12:N0}  {file.Path}  (чанков: {file.Fragments.Count})");
        }
        return 0;
    }

    private static int UnpackAll(Cli.Options options)
    {
        if (options.Positional.Count < 2) return Usage();

        var stopwatch = Stopwatch.StartNew();
        using var unpacker = Open(options.Positional[0]);
        unpacker.UnpackAll(options.Positional[1]);
        stopwatch.Stop();

        Console.WriteLine($"Распаковано в {options.Positional[1]} за {stopwatch.Elapsed.TotalSeconds:F1} с");
        return 0;
    }

    private static int UnpackFile(Cli.Options options)
    {
        if (options.Positional.Count < 2) return Usage();

        using var unpacker = Open(options.Positional[0]);
        unpacker.UnpackFile(options.Positional[1], options.Positional[2]);
        Console.WriteLine($"Файл '{options.Positional[1]}' распакован в {options.Positional[2]}");
        return 0;
    }

    private static int UnpackFolder(Cli.Options options)
    {
        if (options.Positional.Count < 2) return Usage();

        using var unpacker = Open(options.Positional[0]);
        unpacker.UnpackFolder(options.Positional[1], options.Positional[2]);
        Console.WriteLine($"Папка '{options.Positional[1]}' распакована в {options.Positional[2]}");
        return 0;
    }

    private static ContainerUnpacker Open(string containerPath) =>
        new(containerPath, new ZstdCompressor(), new Sha256Hasher());

    private static int Usage()
    {
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Container V3");
        Console.WriteLine("  pack <источник...> -o <container.cc> [-chunk 1M] [-level 3]");
        Console.WriteLine($"     размер чанка по умолчанию {ContainerFormat.DefaultChunkSize:N0} байт (1M), допустимо {ContainerFormat.MinChunkSize:N0}..{ContainerFormat.MaxChunkSize:N0}");
        Console.WriteLine("  add <container.cc> <файл|папка...>  — дописать в готовый контейнер (создаст, если его ещё нет)");
        Console.WriteLine("  delete <container.cc> <имя-в-контейнере|маска...>  — удалить запись; папка удаляется вместе с содержимым");
        Console.WriteLine("     маска работает как в панели: «bin\\*.*» убирает папку bin и всё под ней, «*.txt» — только");
        Console.WriteLine("     файлы в корне контейнера, «bin\\*.log» — файлы bin\\*.log; «..» и диск («C:») по-прежнему запрещены");
        Console.WriteLine("  compact <container.cc>  — выбросить мёртвые чанки и старые индексы");
        Console.WriteLine("     add и delete переписывают только индекс: после delete файл не уменьшается,");
        Console.WriteLine($"     уменьшает его compact. Резервная копия <имя>.bak делается, только если контейнер не больше {ContainerSafety.BackupThresholdBytes / (1024 * 1024)} МБ");
        Console.WriteLine("  info <container.cc>     — заголовок, содержимое и сколько в файле мусора");
        Console.WriteLine("  list <container.cc>");
        Console.WriteLine("  unpack-all <container.cc> <куда>");
        Console.WriteLine("  unpack-file <container.cc> <путь-в-контейнере> <куда>");
        Console.WriteLine("  unpack-folder <container.cc> <папка-в-контейнере> <куда>");
        Console.WriteLine("  selftest");
        Console.WriteLine("  pack-bench <папка...>            — сравнить наполнение чанка: V2-стиль против FileReadPool");
        Console.WriteLine();
        Console.WriteLine("Пример:");
        Console.WriteLine(@"  pack C:\Data\Project -o C:\Out\project.cc -chunk 256K");
        Console.WriteLine(@"  unpack-all C:\Out\project.cc C:\Restored");
    }
}

