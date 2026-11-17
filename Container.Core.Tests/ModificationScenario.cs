using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Modification;
using Container_V3.Packing;

namespace Container_V3.Tests;

/// <summary>
/// Последовательный сценарий изменения контейнера — перенос ModificationCheck из CLI selftest.
///
/// Сценарий принципиально последовательный: каждый шаг опирается на состояние контейнера
/// после предыдущего. xUnit создаёт новый экземпляр класса тестов на каждый [Fact], поэтому
/// состояние живёт здесь, в общем фикстуре (IClassFixture), а тесты только проверяют
/// накопленные ошибки по шагам.
///
/// Сравниваем контейнер с «зеркалом» expected — тем, что мы хотели получить, а не с тем,
/// что упаковывали. Ошибка в любом шаге не прерывает сценарий (как Fail в SelfTest):
/// накопленные расхождения показывает конкретный тест, а не один общий провал.
/// </summary>
public sealed class ModificationScenario : IDisposable
{
    private const int ChunkSize = ContainerFormat.MinChunkSize; // 64K: файлы заведомо делятся на несколько чанков

    private readonly TestWorkspace _ws;
    private readonly string _scratch;
    private readonly string _src1;
    private readonly string _src2;
    private readonly string _src3;
    private readonly string _container;
    private readonly string _expected; // зеркало того, что должно лежать внутри контейнера
    private readonly ContainerModifier _modifier;
    private int _unpackStep;

    /// <summary>Ошибки шага: имя шага → список расхождений (как Fail(ref failures) в SelfTest).</summary>
    public Dictionary<string, List<string>> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>Шаги, которые действительно выполнились. Нужно, чтобы опечатка в имени шага
    /// не превратила тест в пустую проверку «нет ошибок у шага, которого не было».</summary>
    public List<string> ExecutedSteps { get; } = [];

    public ModificationScenario()
    {
        _ws = new TestWorkspace("modify");
        _scratch = Path.Combine(_ws.Root, "modify");
        Directory.CreateDirectory(_scratch);

        _src1 = Path.Combine(_scratch, "src1");
        _src2 = Path.Combine(_scratch, "src2");
        _src3 = Path.Combine(_scratch, "src3");
        _container = Path.Combine(_scratch, "mod.cc");
        _expected = Path.Combine(_scratch, "expected");

        Directory.CreateDirectory(_src1);
        Directory.CreateDirectory(_src2);
        Directory.CreateDirectory(_src3);

        Directory.CreateDirectory(Path.Combine(_src1, "folder", "sub", "emptydir"));
        File.WriteAllText(Path.Combine(_src1, "a.txt"), "первая версия: " + new string('1', 400));
        File.WriteAllText(Path.Combine(_src1, "тест кириллица.txt"), "имя с кириллицей и пробелом");
        TestWorkspace.WriteRandom(Path.Combine(_src1, "big.bin"), 300_000, seed: 11);   // минимум пять чанков
        TestWorkspace.WriteRandom(Path.Combine(_src1, "folder", "b.bin"), 100_000, seed: 12);
        TestWorkspace.WriteRandom(Path.Combine(_src1, "folder", "sub", "c.bin"), 1_000, seed: 13);

        TestWorkspace.CopyTree(_src1, _expected);

        _modifier = new ContainerModifier(
            new Sha256Hasher(), new SourceScanner(),
            new PackingOptions(ChunkSize, 3), level => new ZstdCompressor(level));

        Run();
    }

    public string ContainerPath => _container;

    private void Fail(string step, string message)
    {
        if (!Failures.TryGetValue(step, out var list))
            Failures[step] = list = [];
        list.Add(message);
    }

    private void Check(string step, bool ok, string message)
    {
        if (!ok)
            Fail(step, message);
    }

    private void UnpackCompare(string step)
    {
        try
        {
            TestWorkspace.UnpackCompare(_container, _expected, Path.Combine(_scratch, $"out{_unpackStep++}"));
        }
        catch (Exception ex)
        {
            Fail(step, $"распаковка не совпала с зеркалом: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ContainerHealth Inspect(string container) =>
        ContainerHealth.Inspect(container, new ZstdCompressor(3));

    private void Run()
    {
        // Каждый шаг изолирован: падение одного не съедает расхождения остальных, как Fail()
        // вместо возврата из ModificationCheck в SelfTest.
        RunStep("Step1_Create", Step1_Create);
        RunStep("Step2_Append", Step2_Append);
        RunStep("Step3_Remove", Step3_Remove);
        RunStep("Step4_Compact", Step4_Compact);
        RunStep("Step5_AppendAfterCompact", Step5_AppendAfterCompact);
        RunStep("Step6_EmptyFolderAfterLastFile", Step6_EmptyFolderAfterLastFile);
        RunStep("Step7_DeepestEmptyFolder", Step7_DeepestEmptyFolder);
        RunStep("Step8_RemoveEmptyFolders", Step8_RemoveEmptyFolders);
        RunStep("Step9_EmptyFoldersSurviveCompact", Step9_EmptyFoldersSurviveCompact);
        RunStep("Step10_ContentMask", Step10_ContentMask);
        RunStep("Step11_MaskRefusals", Step11_MaskRefusals);
        RunStep("Step12_Refusals", Step12_Refusals);
        RunStep("BackupRule", BackupRule);
    }

    private void RunStep(string step, Action body)
    {
        ExecutedSteps.Add(step);
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Fail(step, $"исключение: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 1. Контейнер создаётся тем же дописыванием, которым WCX упаковывает в новый .cc.
    private void Step1_Create()
    {
        const string step = "Step1_Create";

        ModificationReport created = _modifier.AppendPaths(_container,
        [
            Path.Combine(_src1, "a.txt"),
            Path.Combine(_src1, "big.bin"),
            Path.Combine(_src1, "folder"),
            Path.Combine(_src1, "тест кириллица.txt"),
        ]);

        Check(step, created.AddedEntries == 6, $"при создании добавлено записей {created.AddedEntries}, ожидалось 6");
        Check(step, created.ReplacedEntries == 0 && created.RemovedEntries == 0, "при создании контейнера есть замены или удаления");
        Check(step, created.Health.FileCount == 6 && created.Health.FolderCount == 1,
            $"созданный контейнер: файлов {created.Health.FileCount}, папок {created.Health.FolderCount}, ожидалось 6 и 1");
        Check(step, created.Health.ChunkCount >= 5, $"big.bin не разбит на чанки: чанков {created.Health.ChunkCount}");
        Check(step, created.Health.ReclaimableBytes == 0, $"в свежем контейнере сразу {created.Health.ReclaimableBytes} байт мусора");
        Check(step, created.BackupPath is null && !File.Exists(ContainerSafety.BackupPath(_container)),
            "при первом создании контейнера появилась резервная копия");

        UnpackCompare(step);
    }

    // 2. Добавление с именами от панели: замена по имени, файл внутрь папки,
    //    которая до этого была записана как пустая.
    private void Step2_Append()
    {
        const string step = "Step2_Append";

        File.WriteAllText(Path.Combine(_src2, "новая версия a.txt"), "вторая версия: " + new string('2', 5_000));
        File.WriteAllText(Path.Combine(_src2, "new.txt"), "новый файл");
        File.WriteAllText(Path.Combine(_src2, "emptydir-content.txt"), "появился внутри папки, которая была пустой");
        TestWorkspace.WriteRandom(Path.Combine(_src2, "bigger.bin"), 200_000, seed: 21);

        long sizeBeforeAdd = new FileInfo(_container).Length;
        string hashBeforeAdd = TestWorkspace.HashOf(_container);

        ModificationReport added = _modifier.Append(_container,
        [
            new ContainerSource(Path.Combine(_src2, "новая версия a.txt"), "a.txt"),
            new ContainerSource(Path.Combine(_src2, "new.txt"), "new.txt"),
            new ContainerSource(Path.Combine(_src2, "bigger.bin"), @"папка юникод\большой.bin"),
            new ContainerSource(Path.Combine(_src2, "emptydir-content.txt"), @"folder\sub\emptydir\внутри.txt"),
        ]);

        Check(step, added.AddedEntries == 4, $"добавлено записей {added.AddedEntries}, ожидалось 4");
        Check(step, added.ReplacedEntries == 1, $"заменено записей {added.ReplacedEntries}, ожидалось 1 (a.txt)");
        Check(step, added.Health.FileCount == 8, $"файлов после добавления {added.Health.FileCount}, ожидалось 8");
        Check(step, added.Health.FolderCount == 0, $"устаревшая запись пустой папки осталась в индексе: {added.Health.FolderCount}");
        Check(step, added.Health.NeedsCompact, "после дописывания уплотнение не предложено, хотя старый индекс стал мусором");
        Check(step, added.ContainerSize > sizeBeforeAdd, $"размер контейнера не вырос: было {sizeBeforeAdd}, стало {added.ContainerSize}");

        // Копия — до записи, и это копия ровно того файла, который был до изменения.
        Check(step, added.BackupPath is not null && File.Exists(added.BackupPath), "резервная копия контейнера не создана");
        if (added.BackupPath is not null)
            Check(step, TestWorkspace.HashOf(added.BackupPath) == hashBeforeAdd, "резервная копия не совпадает с контейнером до изменения");

        TestWorkspace.MirrorFile(_expected, "a.txt", Path.Combine(_src2, "новая версия a.txt"));
        TestWorkspace.MirrorFile(_expected, "new.txt", Path.Combine(_src2, "new.txt"));
        TestWorkspace.MirrorFile(_expected, @"папка юникод\большой.bin", Path.Combine(_src2, "bigger.bin"));
        TestWorkspace.MirrorFile(_expected, @"folder\sub\emptydir\внутри.txt", Path.Combine(_src2, "emptydir-content.txt"));

        UnpackCompare(step);
    }

    // 3. Удаление: папка целиком, файл вложенной папки, и имя в другом регистре.
    private void Step3_Remove()
    {
        const string step = "Step3_Remove";

        ContainerHealth beforeRemove = Inspect(_container);
        long sizeBeforeRemove = beforeRemove.FileSize;

        ModificationReport removed = _modifier.Remove(_container, ["папка юникод", @"folder\sub", "A.TXT"]);

        Check(step, removed.RemovedEntries == 4, $"удалено записей {removed.RemovedEntries}, ожидалось 4");
        Check(step, removed.AddedEntries == 0 && removed.ReplacedEntries == 0, "удаление что-то добавило или заменило");
        Check(step, removed.Health.FileCount == 4, $"файлов после удаления {removed.Health.FileCount}, ожидалось 4");
        Check(step, removed.Health.ChunkCount == beforeRemove.ChunkCount,
            $"список чанков сократился с {beforeRemove.ChunkCount} до {removed.Health.ChunkCount}: номера чанков обязаны сохраняться");
        Check(step, removed.ContainerSize >= sizeBeforeRemove,
            $"контейнер после удаления стал меньше ({removed.ContainerSize} против {sizeBeforeRemove}) — данные переписываются");
        Check(step, removed.Health.NeedsCompact && removed.Health.ReclaimableBytes > 0,
            "удаление не оставило ничего уплотняемого — значит, мусор не посчитан");
        Check(step, removed.Health.EstimatedSizeAfterCompact < removed.ContainerSize, "оценка после уплотнения не меньше текущего размера");
        Check(step, removed.BackupPath is not null, "перед удалением не сделана резервная копия маленького контейнера");

        if (Directory.Exists(Path.Combine(_expected, "папка юникод")))
            Directory.Delete(Path.Combine(_expected, "папка юникод"), true);
        Directory.Delete(Path.Combine(_expected, "folder", "sub"), true);
        File.Delete(Path.Combine(_expected, "a.txt"));

        UnpackCompare(step);
    }

    // 4. Уплотнение: файл меньше, мусора нет, содержимое то же.
    private void Step4_Compact()
    {
        const string step = "Step4_Compact";

        // Состояние до compact нужно для сверки LiveChunks/DroppedChunks — снимаем отдельно.
        ContainerHealth beforeCompact = Inspect(_container);
        long sizeBeforeCompact = new FileInfo(_container).Length;

        CompactReport compact = _modifier.Compact(_container);

        Check(step, compact.Applied, "уплотнение не применили, хотя мусор был");
        Check(step, compact.SizeAfter < sizeBeforeCompact, $"после уплотнения файл не стал меньше: {sizeBeforeCompact} → {compact.SizeAfter}");
        Check(step, compact.Reclaimed > 0, $"сэкономлено {compact.Reclaimed} байт");
        Check(step, compact.LiveChunks == beforeCompact.LiveChunkCount,
            $"перенесено чанков {compact.LiveChunks}, живых до уплотнения было {beforeCompact.LiveChunkCount}");
        Check(step, compact.DroppedChunks == beforeCompact.DeadChunkCount,
            $"сброшено чанков {compact.DroppedChunks}, мёртвых до уплотнения было {beforeCompact.DeadChunkCount}");
        Check(step, new FileInfo(_container).Length == compact.SizeAfter, "размер в отчёте не совпадает с реальным файлом");
        Check(step, compact.BackupPath is not null, "перед уплотнением не сделана резервная копия");

        ContainerHealth compacted = Inspect(_container);
        Check(step, compacted.DeadChunkCount == 0, $"после уплотнения осталось мёртвых чанков {compacted.DeadChunkCount}");
        Check(step, compacted.ChunkCount == compacted.LiveChunkCount,
            $"чанков {compacted.ChunkCount}, живых {compacted.LiveChunkCount} — нумерация не плотная");
        Check(step, compacted.ReclaimableBytes == 0, $"после уплотнения всё ещё есть уплотняемое: {compacted.ReclaimableBytes}");
        Check(step, compacted.FileCount == beforeCompact.FileCount && compacted.FolderCount == beforeCompact.FolderCount,
            "уплотнение потеряло записи");

        // Повторное уплотнение сразу после первого: уплотнять нечего, файл не переписывается.
        CompactReport idle = _modifier.Compact(_container);
        Check(step, !idle.Applied, "повторное уплотнение применили, хотя уплотнять нечего");
        Check(step, idle.SizeAfter == compact.SizeAfter && new FileInfo(_container).Length == compact.SizeAfter,
            $"холостое уплотнение изменило файл: {compact.SizeAfter} → {new FileInfo(_container).Length}");

        UnpackCompare(step);
    }

    // 5. После уплотнения контейнер остаётся пригодным для дописывания.
    private void Step5_AppendAfterCompact()
    {
        const string step = "Step5_AppendAfterCompact";

        File.WriteAllText(Path.Combine(_src3, "after.txt"), "добавлено после уплотнения");

        ModificationReport afterCompact = _modifier.Append(_container,
            [new ContainerSource(Path.Combine(_src3, "after.txt"), @"после уплотнения\after.txt")]);

        Check(step, afterCompact.AddedEntries == 1 && afterCompact.ReplacedEntries == 0,
            $"добавление после уплотнения: {afterCompact.AddedEntries} записей, {afterCompact.ReplacedEntries} замен");
        Check(step, afterCompact.Health.FileCount == 5, $"файлов после добавления {afterCompact.Health.FileCount}, ожидалось 5");

        TestWorkspace.MirrorFile(_expected, @"после уплотнения\after.txt", Path.Combine(_src3, "after.txt"));
        UnpackCompare(step);

        // Дописывание всегда оставляет после себя вытеснённый старый индекс — он и собирается compact.
        Check(step, afterCompact.Health.ReclaimableBytes > 0, "дописывание после уплотнения не оставило вытеснённого индекса");

        long sizeBeforeThird = new FileInfo(_container).Length;
        CompactReport third = _modifier.Compact(_container);

        Check(step, third.Applied, "уплотнение после дописывания не применили");
        Check(step, third.SizeAfter < sizeBeforeThird, $"уплотнение после дописывания не уменьшило файл: {sizeBeforeThird} → {third.SizeAfter}");
        Check(step, Inspect(_container).ReclaimableBytes == 0, "после уплотнения снова осталось уплотняемое");
    }

    // 6. Последний файл в папке удалён: папка остаётся пустой, как в обычной файловой системе.
    //    Иначе «удалил файл» случайно стирал бы саму папку из списка и из распаковки.
    private void Step6_EmptyFolderAfterLastFile()
    {
        const string step = "Step6_EmptyFolderAfterLastFile";

        ModificationReport emptied = _modifier.Remove(_container, [@"folder\b.bin"]);

        Check(step, emptied.RemovedEntries == 1, $"удалено записей {emptied.RemovedEntries}, ожидалось 1");
        Check(step, emptied.PreservedEmptyFolders == 1, $"освободившаяся папка не записана: {emptied.PreservedEmptyFolders}, ожидалось 1");
        Check(step, emptied.Health.FileCount == 5 && emptied.Health.FolderCount == 1,
            $"после удаления файлов {emptied.Health.FileCount}, папок {emptied.Health.FolderCount}, ожидалось 5 и 1");

        File.Delete(Path.Combine(_expected, "folder", "b.bin")); // на диске папка осталась бы пустой
        UnpackCompare(step);
    }

    // Ветка глубже: освобождается самая глубокая папка, а её предки видны через неё.
    private void Step7_DeepestEmptyFolder()
    {
        const string step = "Step7_DeepestEmptyFolder";

        ModificationReport nested = _modifier.Append(_container,
            [new ContainerSource(Path.Combine(_src3, "after.txt"), @"ветка\глубже\всё\после.txt")]);
        Check(step, nested.AddedEntries == 1, $"добавление во вложенную ветку: {nested.AddedEntries} записей, ожидалось 1");

        ModificationReport emptiedBranch = _modifier.Remove(_container, [@"ветка\глубже\всё\после.txt"]);
        Check(step, emptiedBranch.PreservedEmptyFolders == 1,
            $"записей пустых папок добавилось {emptiedBranch.PreservedEmptyFolders}, ожидалось 1 (самая глубокая)");
        Check(step, emptiedBranch.Health.FolderCount == 2,
            $"папок в индексе {emptiedBranch.Health.FolderCount}, ожидалось 2");

        TestWorkspace.MirrorFile(_expected, @"ветка\глубже\всё\после.txt", Path.Combine(_src3, "after.txt"));
        File.Delete(Path.Combine(_expected, "ветка", "глубже", "всё", "после.txt"));
        UnpackCompare(step);
    }

    // Пустую папку можно удалить по имени. Её саму не возрождаем, а предок над ней остаётся пустым.
    private void Step8_RemoveEmptyFolders()
    {
        const string step = "Step8_RemoveEmptyFolders";

        ModificationReport goneFolder = _modifier.Remove(_container, ["folder", @"ветка\глубже"]);

        Check(step, goneFolder.RemovedEntries == 2, $"удаление пустых папок по имени: удалено {goneFolder.RemovedEntries}, ожидалось 2");
        Check(step, goneFolder.PreservedEmptyFolders == 1,
            $"освободившаяся «ветка» не записана: {goneFolder.PreservedEmptyFolders}, ожидалось 1");
        Check(step, goneFolder.Health.FolderCount == 1,
            $"пустых папок в индексе {goneFolder.Health.FolderCount}, ожидалось 1 (только «ветка»)");

        Directory.Delete(Path.Combine(_expected, "folder"), true);
        Directory.Delete(Path.Combine(_expected, "ветка", "глубже"), true);
        UnpackCompare(step);
    }

    // Записи пустых папок переживают уплотнение вместе с плотной перенумерацией чанков.
    private void Step9_EmptyFoldersSurviveCompact()
    {
        const string step = "Step9_EmptyFoldersSurviveCompact";

        ModificationReport refilled = _modifier.Append(_container,
            [new ContainerSource(Path.Combine(_src3, "after.txt"), @"папки\внутри.txt")]);
        Check(step, refilled.AddedEntries == 1, $"добавление в папку после удалений: {refilled.AddedEntries} записей, ожидалось 1");
        Check(step, refilled.Health.FolderCount == 1, $"папок после добавления {refilled.Health.FolderCount}, ожидалось 1 («ветка» осталась)");

        CompactReport withFolders = _modifier.Compact(_container);
        Check(step, withFolders.Applied, "уплотнение после работы с папками не применили");

        ContainerHealth compactedFolders = Inspect(_container);
        Check(step, compactedFolders.FileCount == refilled.Health.FileCount,
            $"записей после уплотнения {compactedFolders.FileCount}, ожидалось {refilled.Health.FileCount}");
        Check(step, compactedFolders.FolderCount == 1,
            $"запись пустой папки пропала при уплотнении: {compactedFolders.FolderCount}, ожидалось 1");

        TestWorkspace.MirrorFile(_expected, @"папки\внутри.txt", Path.Combine(_src3, "after.txt"));
        UnpackCompare(step);
    }

    // Del на папке в панели передаёт маску её содержимого («папки\*.*»), а не имя папки —
    // без разбора таких масок удаление папки в контейнере невозможно.
    // Само правило совпадения масок проверяется отдельно в RemovalMaskTests.
    private void Step10_ContentMask()
    {
        const string step = "Step10_ContentMask";

        ModificationReport masked = _modifier.Remove(_container, [@"папки\*.*"]);

        Check(step, masked.RemovedEntries == 1, $"маска «папки\\*.*» убрала записей {masked.RemovedEntries}, ожидалось 1");
        Check(step, masked.PreservedEmptyFolders == 0,
            $"маска по содержимому папки возродила её как пустую: {masked.PreservedEmptyFolders}, ожидалось 0");
        Check(step, masked.Health.FolderCount == 1,
            $"пустых папок после удаления маской {masked.Health.FolderCount}, ожидалось 1 (осталась только «ветка»)");

        Directory.Delete(Path.Combine(_expected, "папки"), true); // удалены и содержимое, и сама папка
        UnpackCompare(step);
    }

    // Маски, которые нельзя применить: выход за пределы контейнера и отсутствие совпадений.
    private void Step11_MaskRefusals()
    {
        const string step = "Step11_MaskRefusals";
        long before = new FileInfo(_container).Length;

        try
        {
            _modifier.Remove(_container, [@"..\вне\*.*"]);
            Fail(step, "маска с выходом за пределы контейнера прошла молча");
        }
        catch (ContainerFormatException)
        {
        }

        try
        {
            _modifier.Remove(_container, [@"папки\*.nomatch"]);
            Fail(step, "маска, под которую ничего не попало, прошла молча");
        }
        catch (NothingToRemoveException)
        {
        }

        Check(step, new FileInfo(_container).Length == before, "отказ из-за маски изменил контейнер");
    }

    // 7. Отказы: ничего не меняется, если операцию делать нельзя.
    private void Step12_Refusals()
    {
        const string step = "Step12_Refusals";
        long untouched = new FileInfo(_container).Length;

        try
        {
            _modifier.Remove(_container, ["нет-такого-имени.txt"]);
            Fail(step, "удаление несуществующего имени прошло молча");
        }
        catch (NothingToRemoveException)
        {
        }

        Check(step, new FileInfo(_container).Length == untouched, "отказ в удалении изменил контейнер");

        Directory.CreateDirectory(Path.Combine(_src3, "new.txt")); // папка с именем, занятым файлом
        File.WriteAllText(Path.Combine(_src3, "new.txt", "inside.txt"), "внутри");

        try
        {
            _modifier.Append(_container, [new ContainerSource(Path.Combine(_src3, "new.txt"), "new.txt")]);
            Fail(step, "папка с именем существующего файла добавлена без ошибки");
        }
        catch (ContainerFormatException)
        {
        }

        Check(step, new FileInfo(_container).Length == untouched, "отказ из-за конфликта имён изменил контейнер");

        Directory.CreateDirectory(Path.Combine(_src3, "big.bin")); // пустая папка с именем существующего файла

        try
        {
            _modifier.Append(_container, [new ContainerSource(Path.Combine(_src3, "big.bin"), "big.bin")]);
            Fail(step, "пустая папка с именем существующего файла добавлена без ошибки");
        }
        catch (ContainerFormatException)
        {
        }

        Check(step, new FileInfo(_container).Length == untouched, "отказ изменил контейнер");

        UnpackCompare(step);
    }

    // Правило резервной копии: копия только до 100 МБ.
    private void BackupRule()
    {
        const string step = "BackupRule";

        string small = Path.Combine(_scratch, "small.cc");
        File.WriteAllText(small, "маленький контейнер");
        string? smallBackup = ContainerSafety.EnsureBackup(small);

        Check(step, smallBackup is not null, "для контейнера меньше 100 МБ копия не сделана");
        if (smallBackup is not null)
            Check(step, new FileInfo(smallBackup).Length == new FileInfo(small).Length, "размер резервной копии не совпадает с контейнером");

        // Разрежённый файл: 101 МБ длины без записи содержимого — проверка порога, а не диска.
        string large = Path.Combine(_scratch, "large.cc");
        using (var sparse = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            sparse.SetLength(ContainerSafety.BackupThresholdBytes + 1);

        Check(step, ContainerSafety.EnsureBackup(large) is null, "для контейнера больше 100 МБ копия всё-таки сделана");
        Check(step, !File.Exists(ContainerSafety.BackupPath(large)), "файл .bak появился для контейнера больше порога");

        File.Delete(small);
        File.Delete(large);
    }

    public void Dispose() => _ws.Dispose();
}
