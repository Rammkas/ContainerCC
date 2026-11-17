using System.Security.Cryptography;
using System.Text;
using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Modification;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace Container_V3;

/// <summary>
/// Проверка round-trip: упаковал → распаковал → сравнил SHA-256 каждого файла.
/// Именно этого не было в V2, из-за чего баг с ArrayPool (сжатие лишнего хвоста) и тихие нули
/// при коротком чтении не всплывали до распаковки на чужой машине.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "container_v3_selftest");
        string source = Path.Combine(root, "src");
        string restored = Path.Combine(root, "out");
        string container = Path.Combine(root, "test.cc");

        int failures = 0;

        // 64K — нижняя граница, 700K — не степень двойки (ловит ошибку «длина чанка ≠ ChunkSize»),
        // 1M — значение по умолчанию.
        foreach (int chunkSize in new[] { ContainerFormat.MinChunkSize, 700 * 1024, ContainerFormat.DefaultChunkSize })
        {
            Console.WriteLine($"=== chunkSize = {chunkSize} ===");
            Reset(root, source, restored);
            BuildFixture(source);

            try
            {
                using (var packer = new ContainerPacker(
                    new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(),
                    new PackingOptions(chunkSize, 3), () => new ZstdCompressor(3)))
                {
                    packer.Pack(container, [source]);
                }

                using (var unpacker = new ContainerUnpacker(container, new ZstdCompressor(3), new Sha256Hasher()))
                {
                    var info = unpacker.ReadInfo();
                    Console.WriteLine($"  файлов {info.FileCount}, чанков {info.ChunkCount}, chunkSize в заголовке {info.ChunkSize}");

                    if (info.ChunkSize != chunkSize)
                        Fail(ref failures, $"chunkSize в заголовке {info.ChunkSize} != запрошенный {chunkSize}");

                    unpacker.UnpackAll(restored);
                }

                CompareTrees(source, restored, ref failures);
            }
            catch (Exception ex)
            {
                Fail(ref failures, $"исключение: {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine();
        }

        // Отдельная проверка: не-степень-двойки chunkSize и повреждение файла.
        CorruptionCheck(root, container, ref failures);

        DefaultChunkSizeCheck(root, ref failures);

        SourceChangeCheck(root, ref failures);

        ModificationCheck(root, ref failures);

        PathMappingCheck(root, ref failures);

        Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Отображение пути записи (.cc хранит «\») в путь на диске. Проверка нужна до переноса на
    /// Android: там обратный слэш — символ имени файла, и «docs\a.txt» без сборки из сегментов
    /// стал бы одним файлом вместо вложенной папки.
    /// </summary>
    private static void PathMappingCheck(string root, ref int failures)
    {
        Console.WriteLine("=== путь записей контейнера → путь на диске ===");

        string dest = Path.Combine(root, "map");
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);

        // Оба варианта разделителей дают один и тот же путь с настоящей вложенностью.
        string expected = Path.Combine(dest, "docs", "a.txt");
        foreach (string variant in new[] { @"docs\a.txt", "docs/a.txt" })
        {
            string got = ContainerFileSystem.ToAbsolutePath(dest, variant);
            if (got != expected)
                Fail(ref failures, $"ToAbsolutePath('{variant}') дал '{got}', ожидалось '{expected}'");
        }

        string nested = ContainerFileSystem.ToAbsolutePath(dest, @"docs\sub\a.txt");
        if (nested != Path.Combine(dest, "docs", "sub", "a.txt"))
            Fail(ref failures, $"вложенный путь дал '{nested}'");
        Console.WriteLine($"  «docs\\a.txt» и «docs/a.txt» → {Path.GetFileName(Path.GetDirectoryName(nested)!)!}/… — деревом, а не именем файла");

        // Выход за пределы папки распаковки. Прежняя проверка StartsWith пропускала «outside»,
        // когда папка называлась «out»: теперь требуется разделитель сразу после корня.
        string outDir = Path.Combine(root, "out");
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        foreach (string bad in new[] { @"..\outside.txt", @"..\..\outside.txt", "C:evil.txt", @"docs\..\..\x.bin", "\\" })
        {
            try
            {
                string escaped = ContainerFileSystem.ToAbsolutePath(outDir, bad);
                Fail(ref failures, $"путь «{bad}» принят как '{escaped}'");
            }
            catch (ContainerFormatException) { /* так и должно быть */ }
        }
        Console.WriteLine("  «..\\outside.txt», «..\\..\\outside.txt», «C:evil.txt» и пустое имя отклонены");

        // Две записи, ведущие в один файл на диске: без проверки победил бы последний записанный.
        var chunk = new ChunkInfo(0, 0, 0, 10);
        var dupIndex = new ContainerIndex(
            [
                new FileEntry("dup.TXT", 10, false, false, [new ChunkFragmentRef(0, 0, 10)]),
                new FileEntry("dup.txt", 10, false, false, [new ChunkFragmentRef(0, 0, 10)]),
            ],
            [chunk]);

        try
        {
            RestorePlanner.Build(dupIndex, dest);
            Fail(ref failures, "две записи в один файл на диске не пойманы");
        }
        catch (ContainerFormatException ex)
        {
            Console.WriteLine("  «dup.TXT» и «dup.txt» → одна цель: " + ex.Message);
        }

        var okIndex = new ContainerIndex(
            [new FileEntry(@"docs\a.txt", 10, false, false, [new ChunkFragmentRef(0, 0, 10)])],
            [chunk]);
        var built = RestorePlanner.Build(okIndex, dest);
        if (built.Count != 1 || built[0].AbsolutePath != Path.Combine(dest, "docs", "a.txt"))
            Fail(ref failures, $"план распаковки дал {(built.Count == 0 ? "ничего" : built[0].AbsolutePath)}");

        Console.WriteLine();
    }

    private static void BuildFixture(string source)
    {
        Directory.CreateDirectory(source);

        // Файл, который делит чанк пополам (split), и файлы-«наполнители».
        File.WriteAllText(Path.Combine(source, "small.txt"), new string('a', 100));
        File.WriteAllText(Path.Combine(source, "zero.txt"), "");
        Directory.CreateDirectory(Path.Combine(source, "empty_dir"));
        Directory.CreateDirectory(Path.Combine(source, "nested", "deep", "also_empty"));

        // Псевдослучайные несжимаемые данные: проверяют и хвостовой чанк, и split-файл.
        var rng = new Random(12345);
        byte[] big = new byte[3_000_111]; // не кратно ни одному chunkSize
        rng.NextBytes(big);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), big);

        byte[] compressible = Encoding.UTF8.GetBytes(new string('x', 250_000));
        File.WriteAllBytes(Path.Combine(source, "nested", "repeat.dat"), compressible);

        byte[] medium = new byte[131_072]; // ровно 128K — граница чанка при 64K
        rng.NextBytes(medium);
        File.WriteAllBytes(Path.Combine(source, "nested", "deep", "medium.bin"), medium);

        // Имя с кириллицой и пробелом: проверка UTF-8 в индексе.
        File.WriteAllText(Path.Combine(source, "тестовый файл.txt"), "привет, контейнер");
    }

    private static void CompareTrees(string expectedRoot, string actualRoot, ref int failures)
    {
        var expected = Collect(expectedRoot);
        var actual = Collect(actualRoot);

        foreach (var (path, hash) in expected)
        {
            if (!actual.TryGetValue(path, out var actualHash))
            {
                Fail(ref failures, $"нет файла после распаковки: {path}");
                continue;
            }
            if (hash != actualHash)
                Fail(ref failures, $"SHA-256 не совпал: {path}");
        }

        foreach (var path in actual.Keys)
            if (!expected.ContainsKey(path))
                Fail(ref failures, $"лишний файл после распаковки: {path}");

        // Пустые папки обязаны выжить.
        foreach (string dir in Directory.GetDirectories(expectedRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(expectedRoot, dir);
            if (!Directory.Exists(Path.Combine(actualRoot, relative)))
                Fail(ref failures, $"пустая папка потеряна: {relative}");
        }
    }

    private static Dictionary<string, string> Collect(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return map;
    }

    private static void CorruptionCheck(string root, string container, ref int failures)
    {
        Console.WriteLine("=== проверка на повреждение ===");

        if (!File.Exists(container))
        {
            Fail(ref failures, "нет контейнера для проверки повреждения");
            return;
        }

        byte[] bytes = File.ReadAllBytes(container);
        if (bytes.Length <= ContainerHeader.Size + 8)
        {
            Fail(ref failures, "контейнер слишком мал для проверки повреждения");
            return;
        }

        // Портим один байт внутри последнего чанка (перед хвостовым хэшем),
        // чтобы заголовок и индекс остались нетронутыми.
        bytes[^100] ^= 0xFF;
        string damaged = Path.Combine(root, "damaged.cc");
        File.WriteAllBytes(damaged, bytes);

        try
        {
            using var unpacker = new ContainerUnpacker(damaged, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.UnpackAll(Path.Combine(root, "damaged_out"));
            Fail(ref failures, "повреждённый контейнер прочитался без ошибки");
        }
        catch (ChunkIntegrityException)
        {
            Console.WriteLine("  повреждение чанка обнаружено: ChunkIntegrityException");
        }
        catch (ContainerFormatException ex)
        {
            Console.WriteLine($"  повреждение обнаружено через формат: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  повреждение обнаружено: {ex.GetType().Name}");
        }

        // Файл, который не является контейнером.
        File.WriteAllText(Path.Combine(root, "not_a_container.cc"), "hello world");
        try
        {
            using var unpacker = new ContainerUnpacker(Path.Combine(root, "not_a_container.cc"), new ZstdCompressor(3), new Sha256Hasher());
            unpacker.ReadInfo();
            Fail(ref failures, "не-контейнер принят за контейнер");
        }
        catch (ContainerFormatException ex)
        {
            Console.WriteLine($"  чужой файл отклонён: {ex.Message}");
        }

        // Файл старой версии: читается только текущая. Это проверка политики, а не формата —
        // адаптеров V1/V2 здесь нет намеренно.
        byte[] oldVersion = (byte[])bytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oldVersion.AsSpan(4), 2);
        string legacy = Path.Combine(root, "legacy_v2.cc");
        File.WriteAllBytes(legacy, oldVersion);
        try
        {
            using var unpacker = new ContainerUnpacker(legacy, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.ReadInfo();
            Fail(ref failures, "файл старой версии прочитался без ошибки");
        }
        catch (ContainerFormatException ex)
        {
            Console.WriteLine($"  старая версия отклонена: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Проверка значения по умолчанию и границ размера чанка. Отдельно от round-trip, потому что
    /// здесь проверяется не содержимое контейнера, а то, какое число попадает в заголовок, если
    /// пользователь про размер молчит, и что слишком маленький/большой чанк не принимается.
    /// </summary>
    private static void DefaultChunkSizeCheck(string root, ref int failures)
    {
        Console.WriteLine("=== размер чанка по умолчанию и границы ===");

        string scratch = Path.Combine(root, "default_src");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        Directory.CreateDirectory(scratch);
        File.WriteAllBytes(Path.Combine(scratch, "data.bin"), new byte[200_000]);

        // 1. CLI без -chunk: должен подставить ContainerFormat.DefaultChunkSize.
        var noArg = Cli.Parse(["pack", scratch, "-o", "x.cc"]);
        if (noArg.ChunkSize != ContainerFormat.DefaultChunkSize)
            Fail(ref failures, $"CLI без -chunk дал {noArg.ChunkSize}, ожидалось {ContainerFormat.DefaultChunkSize}");
        else
            Console.WriteLine($"  CLI без -chunk: {noArg.ChunkSize:N0} байт");

        // 2. PackingOptions без аргументов — то же значение (значение по умолчанию у record).
        if (new PackingOptions().ChunkSize != ContainerFormat.DefaultChunkSize)
            Fail(ref failures, "PackingOptions() дал не default chunkSize");

        // 3. Упаковка без явного размера пишет в заголовок именно default.
        string container = Path.Combine(root, "default.cc");
        using (var packer = new ContainerPacker(new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(), new PackingOptions()))
            packer.Pack(container, [scratch]);

        using (var unpacker = new ContainerUnpacker(container, new ZstdCompressor(3), new Sha256Hasher()))
        {
            var info = unpacker.ReadInfo();
            if (info.ChunkSize != ContainerFormat.DefaultChunkSize)
                Fail(ref failures, $"в заголовке {info.ChunkSize}, ожидалось {ContainerFormat.DefaultChunkSize}");
            else
                Console.WriteLine($"  в заголовке записано: {info.ChunkSize:N0} байт");
        }

        // 4. Границы: слишком маленький и слишком большой чанк отклоняются, а не молча подменяются.
        foreach (int bad in new[] { 1024, ContainerFormat.MinChunkSize - 1, ContainerFormat.MaxChunkSize + 1 })
        {
            try
            {
                new PackingOptions(bad, 3).Validate();
                Fail(ref failures, $"размер чанка {bad} принят, хотя вне границ");
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine($"  размер {bad} отклонён packing-валидацией");
            }

            try
            {
                ChunkPlanner.Create([new SourceEntry("a", "a", 10, false)], bad);
                Fail(ref failures, $"ChunkPlanner принял размер чанка {bad}");
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine($"  размер {bad} отклонён планировщиком");
            }
        }

        // 5. CLI тоже обязан отказать, а не урезать значение.
        foreach (string bad in new[] { "1K", "32M" })
        {
            try
            {
                Cli.Parse(["pack", "src", "-o", "x.cc", "-chunk", bad]);
                Fail(ref failures, $"CLI принял -chunk {bad}");
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"  -chunk {bad} отклонён: {ex.Message}");
            }
        }

        // 6. Повторная выборочная распаковка в ту же папку. Разные файлы одного каталога могут
        //    пересекаться по чанкам, и если целевой файл уже создан предыдущей командой,
        //    это не должно выглядеть как конфликт.
        string twice = Path.Combine(root, "twice_out");
        if (Directory.Exists(twice)) Directory.Delete(twice, true);
        using (var u = new ContainerUnpacker(container, new ZstdCompressor(3), new Sha256Hasher()))
        {
            u.UnpackFile("data.bin", twice);
            u.UnpackFile("data.bin", twice);
        }
        long written = new FileInfo(Path.Combine(twice, "data.bin")).Length;
        if (written != 200_000)
            Fail(ref failures, $"повторная распаковка одного файла дала {written} байт вместо 200000");
        else
            Console.WriteLine("  повторная распаковка одного файла: перезаписала, размер верный");

        Console.WriteLine();
    }

    /// <summary>
    /// Проверка тихой нулевой дыры. V2 делал "if (read == 0) break;" при заполнении чанка:
    /// контейнер получался целостным по хэшу, но вместо данных лежали нули.
    /// Здесь тот же сценарий воспроизводится намеренно — через фиктивный сканер, который
    /// заявляет файл длиннее, чем он есть на самом деле. Упаковка обязана упасть до записи.
    /// </summary>
    private static void SourceChangeCheck(string root, ref int failures)
    {
        Console.WriteLine("=== источник короче, чем обещано (проверка нулевой дыры) ===");

        string scratch = Path.Combine(root, "hole_src");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        Directory.CreateDirectory(scratch);

        byte[] real = new byte[50_000];
        new Random(7).NextBytes(real);
        File.WriteAllBytes(Path.Combine(scratch, "short.bin"), real);

        var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new OverstatingScanner(scratch),
            new PackingOptions(ContainerFormat.MinChunkSize, 3));

        try
        {
            packer.Pack(Path.Combine(root, "hole.cc"), [scratch]);
            Fail(ref failures, "упаковка прошла несмотря на расхождение размера источника");
        }
        catch (ManifestMismatchException ex)
        {
            Console.WriteLine($"  расхождение обнаружено до записи: {ex.Message}");
        }
        catch (Exception ex)
        {
            Fail(ref failures, $"ожидался ManifestMismatchException, получено {ex.GetType().Name}: {ex.Message}");
        }

        // Незавершённый контейнер не должен оставаться как «готовый»: заголовок с нулевым OffsetToIndex.
        // Отдельная проверка: writer обязан отказаться завершать запись, если индекс не дописан.
        try
        {
            using (var raw = new FileStream(Path.Combine(root, "partial.cc"), FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new ContainerWriter(raw))
            {
                writer.WriteHeader(ContainerFormat.MinChunkSize, 32, 3);
                writer.Write(new byte[1024]); // чанк записали, индекс — нет
            }
            Fail(ref failures, "незавершённая запись прошла без замечания");
        }
        catch (ContainerFormatException ex)
        {
            Console.WriteLine($"  незавершённая запись отклонена: {ex.Message}");
        }

        string partial = Path.Combine(root, "hole.cc");
        if (File.Exists(partial) && new FileInfo(partial).Length > 0)
        {
            try
            {
                using var unpacker = new ContainerUnpacker(partial, new ZstdCompressor(3), new Sha256Hasher());
                unpacker.ReadInfo();
                Fail(ref failures, "недописанный контейнер прочитался как настоящий");
            }
            catch (ContainerFormatException ex)
            {
                Console.WriteLine($"  недописанный файл отклонён: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>Сканер, который завышает размер файла — модель «источник изменился после сканирования».</summary>
    private sealed class OverstatingScanner : ISourceScanner
    {
        private readonly string _root;
        public OverstatingScanner(string root) => _root = root;

        public IEnumerable<SourceEntry> Scan(string path)
        {
            string file = Path.Combine(_root, "short.bin");
            yield return new SourceEntry(file, "short.bin", new FileInfo(file).Length + 10_000, false);
        }
    }

    /// <summary>
    /// Изменение готового контейнера: добавление, замена по имени, удаление файла и папки целиком,
    /// уплотнение, и отдельное правило резервной копии («копия только до 100 МБ»).
    ///
    /// Round-trip этого не показывает. Здесь проверяется то, на чём держится договорённость о
    /// контейнерах в несколько гигабайт: добавление и удаление не переупаковывают файл, поэтому
    /// после удаления он не уменьшается, список чанков сохраняется целиком (номер чанка обязан
    /// совпадать с его позицией — иначе распаковка возьмёт не тот чанк), а после compact
    /// содержимое обязано остаться побайтово тем же.
    /// </summary>
    private static void ModificationCheck(string root, ref int failures)
    {
        Console.WriteLine("=== изменение контейнера: добавление, замена, удаление, уплотнение ===");

        const int chunkSize = ContainerFormat.MinChunkSize; // 64K: файлы заведомо делятся на несколько чанков

        string scratch = Path.Combine(root, "modify");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        Directory.CreateDirectory(scratch);

        string src1 = Path.Combine(scratch, "src1");
        string src2 = Path.Combine(scratch, "src2");
        string src3 = Path.Combine(scratch, "src3");
        string container = Path.Combine(scratch, "mod.cc");

        // Зеркало того, что должно лежать внутри контейнера: те же операции, что и на диске.
        // Сравнивать контейнер с «тем, что мы хотели» честнее, чем с «тем, что упаковывали».
        string expected = Path.Combine(scratch, "expected");

        Directory.CreateDirectory(src1);
        Directory.CreateDirectory(src2);
        Directory.CreateDirectory(src3);

        Directory.CreateDirectory(Path.Combine(src1, "folder", "sub", "emptydir"));
        File.WriteAllText(Path.Combine(src1, "a.txt"), "первая версия: " + new string('1', 400));
        File.WriteAllText(Path.Combine(src1, "тест кириллица.txt"), "имя с кириллицей и пробелом");
        WriteRandom(Path.Combine(src1, "big.bin"), 300_000, seed: 11);   // минимум пять чанков
        WriteRandom(Path.Combine(src1, "folder", "b.bin"), 100_000, seed: 12);
        WriteRandom(Path.Combine(src1, "folder", "sub", "c.bin"), 1_000, seed: 13);

        CopyTree(src1, expected);

        try
        {
            var modifier = new ContainerModifier(
                new Sha256Hasher(), new SourceScanner(),
                new PackingOptions(chunkSize, 3), level => new ZstdCompressor(level));

            // 1. Контейнер создаётся тем же дописыванием, которым WCX упаковывает в новый .cc.
            ModificationReport created = modifier.AppendPaths(container,
            [
                Path.Combine(src1, "a.txt"),
                Path.Combine(src1, "big.bin"),
                Path.Combine(src1, "folder"),
                Path.Combine(src1, "тест кириллица.txt"),
            ]);

            if (created.AddedEntries != 6)
                Fail(ref failures, $"при создании добавлено записей {created.AddedEntries}, ожидалось 6");
            if (created.ReplacedEntries != 0 || created.RemovedEntries != 0)
                Fail(ref failures, "при создании контейнера есть замены или удаления");
            if (created.Health.FileCount != 6 || created.Health.FolderCount != 1)
                Fail(ref failures, $"созданный контейнер: файлов {created.Health.FileCount}, папок {created.Health.FolderCount}, ожидалось 6 и 1");
            if (created.Health.ChunkCount < 5)
                Fail(ref failures, $"big.bin не разбит на чанки: чанков {created.Health.ChunkCount}");
            if (created.Health.ReclaimableBytes != 0)
                Fail(ref failures, $"в свежем контейнере сразу {created.Health.ReclaimableBytes} байт мусора");
            if (created.BackupPath is not null || File.Exists(ContainerSafety.BackupPath(container)))
                Fail(ref failures, "при первом создании контейнера появилась резервная копия");

            PrintHealth(created.Health, "создан");
            UnpackCompare(container, expected, Path.Combine(scratch, "out1"), ref failures);

            // 2. Добавление с именами от панели: замена по имени, папка целиком, файл внутрь
            //    папки, которая до этого была записана как пустая.
            File.WriteAllText(Path.Combine(src2, "новая версия a.txt"), "вторая версия: " + new string('2', 5_000));
            File.WriteAllText(Path.Combine(src2, "new.txt"), "новый файл");
            File.WriteAllText(Path.Combine(src2, "emptydir-content.txt"), "появился внутри папки, которая была пустой");
            WriteRandom(Path.Combine(src2, "bigger.bin"), 200_000, seed: 21);

            long sizeBeforeAdd = new FileInfo(container).Length;
            string hashBeforeAdd = HashOf(container);

            ModificationReport added = modifier.Append(container,
            [
                new ContainerSource(Path.Combine(src2, "новая версия a.txt"), "a.txt"),
                new ContainerSource(Path.Combine(src2, "new.txt"), "new.txt"),
                new ContainerSource(Path.Combine(src2, "bigger.bin"), @"папка юникод\большой.bin"),
                new ContainerSource(Path.Combine(src2, "emptydir-content.txt"), @"folder\sub\emptydir\внутри.txt"),
            ]);

            if (added.AddedEntries != 4)
                Fail(ref failures, $"добавлено записей {added.AddedEntries}, ожидалось 4");
            if (added.ReplacedEntries != 1)
                Fail(ref failures, $"заменено записей {added.ReplacedEntries}, ожидалось 1 (a.txt)");
            if (added.Health.FileCount != 8)
                Fail(ref failures, $"файлов после добавления {added.Health.FileCount}, ожидалось 8");
            if (added.Health.FolderCount != 0)
                Fail(ref failures, $"устаревшая запись пустой папки осталась в индексе: {added.Health.FolderCount}");
            if (!added.Health.NeedsCompact)
                Fail(ref failures, "после дописывания уплотнение не предложено, хотя старый индекс стал мусором");
            if (added.ContainerSize <= sizeBeforeAdd)
                Fail(ref failures, $"размер контейнера не вырос: было {sizeBeforeAdd}, стало {added.ContainerSize}");

            // Копия — до записи, и это копия ровно того файла, который был до изменения.
            if (added.BackupPath is null || !File.Exists(added.BackupPath))
                Fail(ref failures, "резервная копия контейнера не создана");
            else if (HashOf(added.BackupPath) != hashBeforeAdd)
                Fail(ref failures, "резервная копия не совпадает с контейнером до изменения");

            MirrorFile(expected, "a.txt", Path.Combine(src2, "новая версия a.txt"));
            MirrorFile(expected, "new.txt", Path.Combine(src2, "new.txt"));
            MirrorFile(expected, @"папка юникод\большой.bin", Path.Combine(src2, "bigger.bin"));
            MirrorFile(expected, @"folder\sub\emptydir\внутри.txt", Path.Combine(src2, "emptydir-content.txt"));

            PrintHealth(added.Health, "после добавления");
            UnpackCompare(container, expected, Path.Combine(scratch, "out2"), ref failures);

            Console.WriteLine("  имя файла внутри контейнера задано панелью: «папка юникод\\большой.bin» вместо имени с диска");

            // 3. Удаление: папка целиком, файл вложенной папки, и имя в другом регистре.
            ContainerHealth beforeRemove = ContainerHealth.Inspect(container, new ZstdCompressor(3));
            long sizeBeforeRemove = beforeRemove.FileSize;

            ModificationReport removed = modifier.Remove(container, ["папка юникод", @"folder\sub", "A.TXT"]);

            if (removed.RemovedEntries != 4)
                Fail(ref failures, $"удалено записей {removed.RemovedEntries}, ожидалось 4");
            if (removed.AddedEntries != 0 || removed.ReplacedEntries != 0)
                Fail(ref failures, "удаление что-то добавило или заменило");
            if (removed.Health.FileCount != 4)
                Fail(ref failures, $"файлов после удаления {removed.Health.FileCount}, ожидалось 4");
            if (removed.Health.ChunkCount != beforeRemove.ChunkCount)
                Fail(ref failures, $"список чанков сократился с {beforeRemove.ChunkCount} до {removed.Health.ChunkCount}: номера чанков обязаны сохраняться");
            if (removed.ContainerSize < sizeBeforeRemove)
                Fail(ref failures, $"контейнер после удаления стал меньше ({removed.ContainerSize} против {sizeBeforeRemove}) — данные переписываются");
            if (!removed.Health.NeedsCompact || removed.Health.ReclaimableBytes <= 0)
                Fail(ref failures, "удаление не оставило ничего уплотняемого — значит, мусор не посчитан");
            if (removed.Health.EstimatedSizeAfterCompact >= removed.ContainerSize)
                Fail(ref failures, "оценка после уплотнения не меньше текущего размера");
            if (removed.BackupPath is null)
                Fail(ref failures, "перед удалением не сделана резервная копия маленького контейнера");

            if (Directory.Exists(Path.Combine(expected, "папка юникод"))) Directory.Delete(Path.Combine(expected, "папка юникод"), true);
            Directory.Delete(Path.Combine(expected, "folder", "sub"), true);
            File.Delete(Path.Combine(expected, "a.txt"));

            PrintHealth(removed.Health, "после удаления");
            Console.WriteLine($"  после удаления файл не уменьшился: {sizeBeforeRemove} → {removed.ContainerSize} байт; уплотнение вернёт ~{removed.Health.EstimatedSizeAfterCompact}");
            UnpackCompare(container, expected, Path.Combine(scratch, "out3"), ref failures);

            // 4. Уплотнение: файл меньше, мусора нет, содержимое то же.
            long sizeBeforeCompact = new FileInfo(container).Length;

            CompactReport compact = modifier.Compact(container);

            if (!compact.Applied)
                Fail(ref failures, "уплотнение не применили, хотя мусор был");
            if (compact.SizeAfter >= sizeBeforeCompact)
                Fail(ref failures, $"после уплотнения файл не стал меньше: {sizeBeforeCompact} → {compact.SizeAfter}");
            if (compact.Reclaimed <= 0)
                Fail(ref failures, $"сэкономлено {compact.Reclaimed} байт");
            if (compact.LiveChunks != removed.Health.LiveChunkCount)
                Fail(ref failures, $"перенесено чанков {compact.LiveChunks}, живых после удаления было {removed.Health.LiveChunkCount}");
            if (compact.DroppedChunks != removed.Health.DeadChunkCount)
                Fail(ref failures, $"сброшено чанков {compact.DroppedChunks}, мёртвых после удаления было {removed.Health.DeadChunkCount}");
            if (new FileInfo(container).Length != compact.SizeAfter)
                Fail(ref failures, "размер в отчёте не совпадает с реальным файлом");
            if (compact.BackupPath is null)
                Fail(ref failures, "перед уплотнением не сделана резервная копия");

            ContainerHealth compacted = ContainerHealth.Inspect(container, new ZstdCompressor(3));
            if (compacted.DeadChunkCount != 0)
                Fail(ref failures, $"после уплотнения осталось мёртвых чанков {compacted.DeadChunkCount}");
            if (compacted.ChunkCount != compacted.LiveChunkCount)
                Fail(ref failures, $"чанков {compacted.ChunkCount}, живых {compacted.LiveChunkCount} — нумерация не плотная");
            if (compacted.ReclaimableBytes != 0)
                Fail(ref failures, $"после уплотнения всё ещё есть уплотняемое: {compacted.ReclaimableBytes}");
            if (compacted.FileCount != removed.Health.FileCount || compacted.FolderCount != removed.Health.FolderCount)
                Fail(ref failures, "уплотнение потеряло записи");

            // Повторное уплотнение сразу после первого: уплотнять нечего, файл не переписывается.
            CompactReport idle = modifier.Compact(container);
            if (idle.Applied)
                Fail(ref failures, "повторное уплотнение применили, хотя уплотнять нечего");
            if (idle.SizeAfter != compact.SizeAfter || new FileInfo(container).Length != compact.SizeAfter)
                Fail(ref failures, $"холостое уплотнение изменило файл: {compact.SizeAfter} → {new FileInfo(container).Length}");
            else
                Console.WriteLine("  повторное compact: уплотнять нечего, файл не переписан");

            PrintHealth(compacted, "после уплотнения");
            Console.WriteLine($"  уплотнение: {sizeBeforeCompact} → {compact.SizeAfter} байт, сэкономлено {compact.Reclaimed}, сброшено чанков {compact.DroppedChunks}");
            UnpackCompare(container, expected, Path.Combine(scratch, "out4"), ref failures);

            // 5. После уплотнения контейнер остаётся пригодным для дописывания.
            Directory.CreateDirectory(src3);
            File.WriteAllText(Path.Combine(src3, "after.txt"), "добавлено после уплотнения");

            ModificationReport afterCompact = modifier.Append(container,
                [new ContainerSource(Path.Combine(src3, "after.txt"), @"после уплотнения\after.txt")]);

            if (afterCompact.AddedEntries != 1 || afterCompact.ReplacedEntries != 0)
                Fail(ref failures, $"добавление после уплотнения: {afterCompact.AddedEntries} записей, {afterCompact.ReplacedEntries} замен");
            if (afterCompact.Health.FileCount != 5)
                Fail(ref failures, $"файлов после добавления {afterCompact.Health.FileCount}, ожидалось 5");

            MirrorFile(expected, @"после уплотнения\after.txt", Path.Combine(src3, "after.txt"));
            UnpackCompare(container, expected, Path.Combine(scratch, "out5"), ref failures);

            // Дописывание всегда оставляет после себя вытеснённый старый индекс — он и собирается compact.
            if (afterCompact.Health.ReclaimableBytes <= 0)
                Fail(ref failures, "дописывание после уплотнения не оставило вытеснённого индекса");

            long sizeBeforeThird = new FileInfo(container).Length;
            CompactReport third = modifier.Compact(container);
            if (!third.Applied)
                Fail(ref failures, "уплотнение после дописывания не применили");
            if (third.SizeAfter >= sizeBeforeThird)
                Fail(ref failures, $"уплотнение после дописывания не уменьшило файл: {sizeBeforeThird} → {third.SizeAfter}");
            if (ContainerHealth.Inspect(container, new ZstdCompressor(3)).ReclaimableBytes != 0)
                Fail(ref failures, "после уплотнения снова осталось уплотняемое");
            else
                Console.WriteLine($"  compact после дописывания: {sizeBeforeThird} → {third.SizeAfter} байт");

            // 6. Последний файл в папке удалён: папка остаётся пустой, как в обычной файловой системе.
            //    Иначе «удалил файл» случайно стирал бы саму папку из списка и из распаковки.
            ModificationReport emptied = modifier.Remove(container, [@"folder\b.bin"]);

            if (emptied.RemovedEntries != 1)
                Fail(ref failures, $"удалено записей {emptied.RemovedEntries}, ожидалось 1");
            if (emptied.PreservedEmptyFolders != 1)
                Fail(ref failures, $"освободившаяся папка не записана: {emptied.PreservedEmptyFolders}, ожидалось 1");
            if (emptied.Health.FileCount != 5 || emptied.Health.FolderCount != 1)
                Fail(ref failures, $"после удаления файлов {emptied.Health.FileCount}, папок {emptied.Health.FolderCount}, ожидалось 5 и 1");

            File.Delete(Path.Combine(expected, "folder", "b.bin"));   // на диске папка осталась бы пустой
            Console.WriteLine("  «folder\\b.bin» удалён: папка «folder» осталась в контейнере пустой");
            PrintHealth(emptied.Health, "после удаления последнего файла из папки");
            UnpackCompare(container, expected, Path.Combine(scratch, "out6"), ref failures);

            // Ветка глубже: освобождается самая глубокая папка, а её предки видны через неё.
            ModificationReport nested = modifier.Append(container,
                [new ContainerSource(Path.Combine(src3, "after.txt"), @"ветка\глубже\всё\после.txt")]);
            if (nested.AddedEntries != 1)
                Fail(ref failures, $"добавление во вложенную ветку: {nested.AddedEntries} записей, ожидалось 1");

            ModificationReport emptiedBranch = modifier.Remove(container, [@"ветка\глубже\всё\после.txt"]);
            if (emptiedBranch.PreservedEmptyFolders != 1)
                Fail(ref failures, $"записей пустых папок добавилось {emptiedBranch.PreservedEmptyFolders}, ожидалось 1 (самая глубокая)");
            if (emptiedBranch.Health.FolderCount != 2)
                Fail(ref failures, $"папок в индексе {emptiedBranch.Health.FolderCount}, ожидалось 2");

            MirrorFile(expected, @"ветка\глубже\всё\после.txt", Path.Combine(src3, "after.txt"));
            File.Delete(Path.Combine(expected, "ветка", "глубже", "всё", "после.txt"));
            UnpackCompare(container, expected, Path.Combine(scratch, "out7"), ref failures);
            Console.WriteLine("  «ветка\\глубже\\всё\\после.txt» удалён: «ветка» и «ветка\\глубже» видны через запись о самой глубокой папке");

            // Пустую папку можно удалить по имени. Её саму не возрождаем, а предок над ней остаётся
            // пустым — как на диске, если удалить из «ветка» подпапку «глубже».
            ModificationReport goneFolder = modifier.Remove(container, ["folder", @"ветка\глубже"]);
            if (goneFolder.RemovedEntries != 2)
                Fail(ref failures, $"удаление пустых папок по имени: удалено {goneFolder.RemovedEntries}, ожидалось 2");
            if (goneFolder.PreservedEmptyFolders != 1)
                Fail(ref failures, $"освободившаяся «ветка» не записана: {goneFolder.PreservedEmptyFolders}, ожидалось 1");
            if (goneFolder.Health.FolderCount != 1)
                Fail(ref failures, $"пустых папок в индексе {goneFolder.Health.FolderCount}, ожидалось 1 (только «ветка»)");

            Directory.Delete(Path.Combine(expected, "folder"), true);
            Directory.Delete(Path.Combine(expected, "ветка", "глубже"), true);
            UnpackCompare(container, expected, Path.Combine(scratch, "out8"), ref failures);

            // Записи пустых папок переживают уплотнение вместе с плотной перенумерацией чанков.
            ModificationReport refilled = modifier.Append(container,
                [new ContainerSource(Path.Combine(src3, "after.txt"), @"папки\внутри.txt")]);
            if (refilled.AddedEntries != 1)
                Fail(ref failures, $"добавление в папку после удалений: {refilled.AddedEntries} записей, ожидалось 1");
            if (refilled.Health.FolderCount != 1)
                Fail(ref failures, $"папок после добавления {refilled.Health.FolderCount}, ожидалось 1 («ветка» осталась)");

            CompactReport withFolders = modifier.Compact(container);
            if (!withFolders.Applied)
                Fail(ref failures, "уплотнение после работы с папками не применили");

            ContainerHealth compactedFolders = ContainerHealth.Inspect(container, new ZstdCompressor(3));
            if (compactedFolders.FileCount != refilled.Health.FileCount)
                Fail(ref failures, $"записей после уплотнения {compactedFolders.FileCount}, ожидалось {refilled.Health.FileCount}");
            if (compactedFolders.FolderCount != 1)
                Fail(ref failures, $"запись пустой папки пропала при уплотнении: {compactedFolders.FolderCount}, ожидалось 1");

            MirrorFile(expected, @"папки\внутри.txt", Path.Combine(src3, "after.txt"));
            UnpackCompare(container, expected, Path.Combine(scratch, "out9"), ref failures);

            // 6.8. Имена и маски удаления. Del на папке Total Commander передаёт не имя папки,
            //      а маску её содержимого — «MauiApp1\*.*»: без разбора таких масок удаление папки
            //      в контейнере невозможно («*» и «?» запрещены в имени записи).
            CheckMasks(ref failures);

            ModificationReport masked = modifier.Remove(container, [@"папки\*.*"]);
            if (masked.RemovedEntries != 1)
                Fail(ref failures, $"маска «папки\\*.*» убрала записей {masked.RemovedEntries}, ожидалось 1");
            if (masked.PreservedEmptyFolders != 0)
                Fail(ref failures, $"маска по содержимому папки возродила её как пустую: {masked.PreservedEmptyFolders}, ожидалось 0");
            if (masked.Health.FolderCount != 1)
                Fail(ref failures, $"пустых папок после удаления маской {masked.Health.FolderCount}, ожидалось 1 (осталась только «ветка»)");

            Directory.Delete(Path.Combine(expected, "папки"), true);   // удалены и содержимое, и сама папка
            UnpackCompare(container, expected, Path.Combine(scratch, "out9m"), ref failures);
            Console.WriteLine("  маска «папки\\*.*» (Del на папке): содержимое и сама папка удалены, «ветка» на месте");

            long maskRefusals = new FileInfo(container).Length;

            try
            {
                modifier.Remove(container, [@"..\вне\*.*"]);
                Fail(ref failures, "маска с выходом за пределы контейнера прошла молча");
            }
            catch (ContainerFormatException)
            {
                Console.WriteLine("  маска «..\\вне\\*.*» отклонена: из контейнера нельзя удалять то, что вне его");
            }

            try
            {
                modifier.Remove(container, [@"папки\*.nomatch"]);
                Fail(ref failures, "маска, под которую ничего не попало, прошла молча");
            }
            catch (NothingToRemoveException)
            {
                Console.WriteLine("  маска без совпадений: NothingToRemoveException (в панели — «нет файлов для удаления»)");
            }

            if (new FileInfo(container).Length != maskRefusals)
                Fail(ref failures, "отказ из-за маски изменил контейнер");

            // 7. Отказы: ничего не меняется, если операцию делать нельзя.
            long untouched = new FileInfo(container).Length;

            try
            {
                modifier.Remove(container, ["нет-такого-имени.txt"]);
                Fail(ref failures, "удаление несуществующего имени прошло молча");
            }
            catch (NothingToRemoveException)
            {
                Console.WriteLine("  удаление несуществующего имени: NothingToRemoveException (в панели — «нет файлов для удаления»)");
            }

            if (new FileInfo(container).Length != untouched)
                Fail(ref failures, "отказ в удалении изменил контейнер");

            Directory.CreateDirectory(Path.Combine(src3, "new.txt"));   // папка с именем, занятым файлом
            File.WriteAllText(Path.Combine(src3, "new.txt", "inside.txt"), "внутри");

            try
            {
                modifier.Append(container, [new ContainerSource(Path.Combine(src3, "new.txt"), "new.txt")]);
                Fail(ref failures, "папка с именем существующего файла добавлена без ошибки");
            }
            catch (ContainerFormatException ex)
            {
                Console.WriteLine("  конфликт имени файла и папки отклонён: " + ex.Message);
            }

            if (new FileInfo(container).Length != untouched)
                Fail(ref failures, "отказ из-за конфликта имён изменил контейнер");

            Directory.CreateDirectory(Path.Combine(src3, "big.bin"));   // пустая папка с именем существующего файла

            try
            {
                modifier.Append(container, [new ContainerSource(Path.Combine(src3, "big.bin"), "big.bin")]);
                Fail(ref failures, "пустая папка с именем существующего файла добавлена без ошибки");
            }
            catch (ContainerFormatException)
            {
                Console.WriteLine("  пустая папка с именем существующего файла тоже отклонена");
            }

            if (new FileInfo(container).Length != untouched)
                Fail(ref failures, "отказ изменил контейнер");

            UnpackCompare(container, expected, Path.Combine(scratch, "out6"), ref failures);

            // 7. Правило резервной копии: копия только до 100 МБ.
            string small = Path.Combine(scratch, "small.cc");
            File.WriteAllText(small, "маленький контейнер");
            string? smallBackup = ContainerSafety.EnsureBackup(small);

            if (smallBackup is null)
                Fail(ref failures, "для контейнера меньше 100 МБ копия не сделана");
            else if (new FileInfo(smallBackup).Length != new FileInfo(small).Length)
                Fail(ref failures, "размер резервной копии не совпадает с контейнером");
            else
                Console.WriteLine($"  копия есть: {Path.GetFileName(smallBackup)}");

            // Разрежённый файл: 101 МБ длины без записи содержимого — проверка порога, а не диска.
            string large = Path.Combine(scratch, "large.cc");
            using (var sparse = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
                sparse.SetLength(ContainerSafety.BackupThresholdBytes + 1);

            if (ContainerSafety.EnsureBackup(large) is not null)
                Fail(ref failures, "для контейнера больше 100 МБ копия всё-таки сделана");
            if (File.Exists(ContainerSafety.BackupPath(large)))
                Fail(ref failures, "файл .bak появился для контейнера больше порога");
            else
                Console.WriteLine($"  контейнер {ContainerSafety.BackupThresholdBytes + 1} байт: копию не делаем");

            File.Delete(small);
            File.Delete(large);
        }
        catch (Exception ex)
        {
            Fail(ref failures, $"исключение: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Разбор имён и масок удаления: как строка из списка на удаление — из панели Total Commander
    /// или из CLI — описывает записи контейнера. Контейнер здесь не открывается: проверяется
    /// само правило совпадения.
    /// </summary>
    private static void CheckMasks(ref int failures)
    {
        RemovalPattern contents = RemovalPattern.Parse(@"bin\tools\*.*");
        if (contents.Scope != RemovalScope.Folder || contents.Folder != @"bin\tools")
            Fail(ref failures, $"«bin\\tools\\*.*» разобрана как {contents.Scope} с папкой «{contents.Folder}», ожидался Folder с папкой «bin\\tools»");

        if (RemovalPattern.Parse(@"bin\*.log").Scope != RemovalScope.Mask)
            Fail(ref failures, "«bin\\*.log» должна быть маской имён, а не удалением папки вместе с её поддеревом");

        if (RemovalPattern.Parse("A.TXT").Scope != RemovalScope.Exact)
            Fail(ref failures, "«A.TXT» должна оставаться обычным именем");

        RemovalPattern root = RemovalPattern.Parse(@"*.*");
        if (root.Scope != RemovalScope.Folder || root.Folder.Length != 0)
            Fail(ref failures, "«*.*» в корне панели должна описывать всё содержимое контейнера");

        if (RemovalPattern.Parse("bin//tools/*.log").Name != @"bin\tools\*.log")
            Fail(ref failures, $"маска нормализована как «{RemovalPattern.Parse("bin//tools/*.log").Name}», ожидалось «bin\\tools\\*.log»");

        (string Mask, string Path, bool Expected)[] cases =
        [
            (@"bin\tools\*.*", @"bin\tools", true),
            (@"bin\tools\*.*", @"bin\tools\a\b.txt", true),
            (@"bin\tools\*.*", @"BIN\TOOLS\A.TXT", true),
            (@"bin\tools\*.*", @"bin", false),
            (@"bin\tools\*.*", @"bin\other.txt", false),
            (@"bin\tools\*.*", @"bin\toolsx\a.txt", false),
            (@"bin\tools\*", @"bin\tools\глубоко\файл.bin", true),
            (@"bin\*.*", @"bin", true),
            (@"bin\*.*", @"bin\tools\helper.log", true),
            (@"bin\*.*", @"other\bin\x.txt", false),
            (@"*.*", @"глубокая\ветка\файл.txt", true),
            (@"bin\*.log", @"bin\notes.log", true),
            (@"bin\*.log", @"bin\sub\notes.log", false),
            (@"bin\*.log", @"binx\notes.log", false),
            (@"bin\*.log", @"bin\notes.logx", false),
            (@"bin\app.tx?", @"bin\app.txt", true),
            (@"bin\app.tx?", @"bin\app.tx", false),
            (@"bin\app.tx?", @"bin\app.txtx", false),
            (@"*.txt", @"отчёт.txt", true),
            (@"*.txt", @"bin\app.txt", false),
            (@"папка юникод", @"папка юникод", true),
            (@"папка юникод", @"Папка ЮНИКОД\внутри.txt", true),
            (@"папка юникод", @"папка юникода.txt", false),
        ];

        foreach ((string mask, string path, bool expected) in cases)
        {
            if (RemovalPattern.Parse(mask).Matches(path) != expected)
                Fail(ref failures, $"маска «{mask}» должна {(expected ? "" : "не ")}попадать в «{path}»");
        }

        string[] rejected =
        [
            @"..\вне\*.*",
            @"bin\..\x",
            @"C:\bin\*.*",
            @"bin<>tools\*",
            "bin|tools\\*",
            "   ",
            "",
        ];

        foreach (string mask in rejected)
        {
            try
            {
                RemovalPattern.Parse(mask);
                Fail(ref failures, $"«{mask}» принята как маска удаления, а её принимать нельзя");
            }
            catch (ContainerFormatException)
            {
            }
        }

        Console.WriteLine("  маски удаления разобраны: «папка\\*.*» = папка целиком, «bin\\*.log» = файлы папки, «*.txt» = файлы в корне");
    }

    private static void WriteRandom(string path, int size, int seed)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void PrintHealth(ContainerHealth health, string stage) =>
        Console.WriteLine($"  {stage}: файлов {health.FileCount}, папок {health.FolderCount}, чанков {health.ChunkCount} " +
            $"(живых {health.LiveChunkCount}, мёртвых {health.DeadChunkCount}), размер {health.FileSize}, уплотняемого {health.ReclaimableBytes}");

    private static void MirrorFile(string mirrorRoot, string relative, string sourceFile)
    {
        string target = Path.Combine(mirrorRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceFile, target, overwrite: true);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        // Пустые папки создаются отдельным проходом: GetFiles их не возвращает, а структура дерева держится на них.
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void UnpackCompare(string container, string expectedRoot, string target, ref int failures)
    {
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);

        using (var unpacker = new ContainerUnpacker(container, new ZstdCompressor(3), new Sha256Hasher()))
            unpacker.UnpackAll(target);

        CompareTrees(expectedRoot, target, ref failures);
    }

    private static void Reset(string root, string source, string restored)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(restored);
    }

    private static void Fail(ref int failures, string message)
    {
        failures++;
        Console.WriteLine("  ОШИБКА: " + message);
    }
}
