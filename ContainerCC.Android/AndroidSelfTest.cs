using Android.Util;

namespace ContainerCC.Mobile;

/// <summary>
/// Проверка ядра формата прямо на Android: он запускается на телефоне или эмуляторе командой
///
///   adb shell am start -n com.containercc/<activity> --es selftest 1
///
/// и пишет результат в журнал устройства (adb logcat -s ContainerCC). Нужен именно здесь, а не
/// в общем selftest на Windows, потому что спорный момент — отображение путей: записи в .cc
/// хранятся с «\», и только на Android видно, что распаковка даёт настоящие вложенные папки,
/// а не файл с именем «docs\a.txt».
///
/// Рабочая папка — общая память (Download/containercc-selftest): её видно через adb без root,
/// и она же используется приложению в реальной работе.
///
/// Важно про имена. «Упаковать выбранное» в приложении делает то же, что копирование папки
/// в архив в Total Commander: папка сохраняет своё имя внутри контейнера (src\a.txt),
/// а не разбирается на содержимое, как команда pack в командной строке.
/// </summary>
public static class AndroidSelfTest
{
    private const string Tag = "ContainerCC";

    public static void RunIfRequested(Android.Content.Intent? intent)
    {
        if (intent?.GetStringExtra("selftest") != "1")
        {
            // Второй режим: открыть контейнер, созданный на Windows (CLI или WCX-плагин),
            // распаковать его на телефоне и проверить целостность. Это проверка совместимости,
            // а не поведения приложения.
            string? open = intent?.GetStringExtra("open");
            if (!string.IsNullOrWhiteSpace(open))
            {
                Task.Run(() =>
                {
                    try { OpenOnly(open!); }
                    catch (Exception ex) { Log.Error(Tag, "OPEN EXCEPTION: " + ex); }
                });
            }

            return;
        }

        Task.Run(() =>
        {
            try
            {
                int failures = Run();
                Log.Info(Tag, failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED: {failures}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "SELFTEST EXCEPTION: " + ex);
            }
        });
    }

    private static int Run()
    {
        int failures = 0;

        string shared = StorageAccess.StartDirectory();
        string root = Path.Combine(shared, "Download", "containercc-selftest");
        Log.Info(Tag, "рабочая папка: " + root + (StorageAccess.HasAllFilesAccess() ? " (доступ ко всем файлам есть)" : " (папка приложения)"));

        if (Directory.Exists(root))
            Directory.Delete(root, true);

        string source = Path.Combine(root, "src");
        string restored = Path.Combine(root, "out");
        string container = Path.Combine(root, "test.cc");
        Directory.CreateDirectory(source);

        // Вложенность и кириллица — то, что должно превратиться в папки на Android.
        File.WriteAllText(Path.Combine(source, "a.txt"), "привет, контейнер");
        Directory.CreateDirectory(Path.Combine(source, "docs", "sub"));
        File.WriteAllBytes(Path.Combine(source, "docs", "b.bin"), Repeat(7, 300_000));
        File.WriteAllBytes(Path.Combine(source, "docs", "sub", "c.bin"), Repeat(11, 1_200));
        Directory.CreateDirectory(Path.Combine(source, "docs", "пустая"));
        File.WriteAllText(Path.Combine(source, "docs", "срез.txt"), new string('ы', 50_000));

        var service = new ContainerService();

        // 1. Создание контейнера (та же операция, что «Упаковать выбранное» в интерфейсе).
        var created = service.Append(container, [source], _ => { }, () => false);
        Log.Info(Tag, $"упаковано: записей {created.AddedEntries}, чанков {created.AddedChunks}, размер файла {created.ContainerSize:N0}");

        var (info, health) = service.Inspect(container);
        Log.Info(Tag, $"контейнер: файлов {info.FileCount}, чанков {info.ChunkCount}, чанк {info.ChunkSize:N0} байт, уровень {info.CompressionLevel}, мусора {health.ReclaimableBytes:N0}");

        // 2. Как контейнер виден по частям: корень, затем docs, затем docs/sub.
        LogTree(service, container, "");
        LogTree(service, container, @"src\docs");

        // 3. Полная распаковка: проверяем именно дерево каталогов на диске телефона.
        service.ExtractAll(container, restored, _ => { });

        string src = Path.Combine(restored, "src");

        if (!Directory.Exists(Path.Combine(src, "docs", "sub")))
            Fail(ref failures, "нет вложенной папки src/docs/sub: распаковка не построила дерево каталогов");

        foreach (string stray in Directory.EnumerateFileSystemEntries(restored, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            if (stray.Contains('\\'))
                Fail(ref failures, "на диске телефона появилась запись с обратным слэшем в имени: " + stray);
        }

        if (!Directory.Exists(Path.Combine(src, "docs", "пустая")))
            Fail(ref failures, "пустая папка src/docs/пустая потерялась при распаковке");

        if (CompareTrees(source, src) != 0)
            Fail(ref failures, "содержимое после распаковки не совпадает побайтово");
        else
            Log.Info(Tag, "дерево восстановлено: src/docs/sub/c.bin и «src/docs/срез.txt» — настоящая вложенность, содержимое совпадает");

        // 4. Выборочная распаковка одной папки (с отчётом и отменой между файлами).
        string folderOut = Path.Combine(root, "out-folder");
        int extracted = service.Extract(container, ["src\\docs"], folderOut, msg => Log.Info(Tag, "  " + msg), () => false);
        Log.Info(Tag, $"выборочно извлечено записей: {extracted}");
        // Структура внутри контейнера сохраняется (как при «Extract» в Total Commander):
        // src\docs\b.bin оказывается в out-folder\src\docs\b.bin, а не в out-folder\docs\b.bin.
        if (!File.Exists(Path.Combine(folderOut, "src", "docs", "sub", "c.bin")))
            Fail(ref failures, "выборочная распаковка папки не восстановила src/docs/sub/c.bin");

        // 5. Добавление нового файла в готовый контейнер.
        string extra = Path.Combine(root, "added.txt");
        File.WriteAllText(extra, "добавлено на телефоне");
        var appended = service.Append(container, [extra], msg => Log.Info(Tag, "  " + msg), () => false);
        Log.Info(Tag, $"добавлено: записей {appended.AddedEntries}, контейнер {appended.ContainerSize:N0}, "
            + $"резервная копия {(appended.BackupPath is null ? "не делалась" : Path.GetFileName(appended.BackupPath))}");

        // 5b. Удаление по маске — те же маски, что понимает плагин в Total Commander.
        string log1 = Path.Combine(root, "one.log");
        string log2 = Path.Combine(root, "two.log");
        File.WriteAllText(log1, "журнал 1");
        File.WriteAllText(log2, "журнал 2");
        service.Append(container, [log1, log2], _ => { }, () => false);

        var (matched, sample) = service.MatchMask(container, "*.log");
        Log.Info(Tag, $"маска «*.log» затрагивает {matched} записей: {string.Join(", ", sample)}");
        if (matched != 2)
            Fail(ref failures, $"маска «*.log» должна затрагивать 2 записи, видим {matched}");

        var masked = service.Remove(container, ["*.log"]);
        Log.Info(Tag, $"удалено по маске записей: {masked.RemovedEntries}");
        if (masked.RemovedEntries != 2)
            Fail(ref failures, "удаление по маске убирает не то число записей");

        var leftAfterMask = service.Children(container, "");
        if (leftAfterMask.Any(r => r.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            Fail(ref failures, "после удаления по маске в контейнере остались .log-записи: "
                + string.Join(", ", leftAfterMask.Select(r => r.Name)));
        else
            Log.Info(Tag, "после удаления по маске в корне: " + string.Join(", ", leftAfterMask.Select(r => r.Name)));

        // 6. Удаление папки вместе с содержимым (как Del на папке в Total Commander).
        var removed = service.Remove(container, [@"src\docs"]);
        Log.Info(Tag, $"удалено: записей {removed.RemovedEntries}, контейнер {removed.ContainerSize:N0}, "
            + $"уплотняемого {removed.Health.ReclaimableBytes:N0}");

        var afterRemove = service.Inspect(container);
        if (afterRemove.Info.FileCount != 2)
            Fail(ref failures, $"после удаления «src\\docs» должны остаться a.txt и added.txt, видимых файлов {afterRemove.Info.FileCount}");

        // 7. Уплотнение: файл обязан уменьшиться ровно на мёртвые чанки.
        long before = new FileInfo(container).Length;
        var compacted = service.Compact(container);
        Log.Info(Tag, $"уплотнение: {compacted.SizeBefore:N0} → {compacted.SizeAfter:N0}, освобождено {compacted.Reclaimed:N0}, сброшено чанков {compacted.DroppedChunks}");
        if (!compacted.Applied || compacted.SizeAfter >= before)
            Fail(ref failures, "уплотнение не уменьшило файл");

        // 8. Проверка целостности и повторная распаковка уплотнённого контейнера.
        int verified = service.Verify(container, [], msg => Log.Info(Tag, "  " + msg), () => false);
        Log.Info(Tag, $"проверено записей: {verified}");

        string restored2 = Path.Combine(root, "out2");
        service.ExtractAll(container, restored2, _ => { });
        if (!File.Exists(Path.Combine(restored2, "added.txt")) || !File.Exists(Path.Combine(restored2, "src", "a.txt")))
            Fail(ref failures, "после уплотнения контейнер распаковывается не полностью");

        Log.Info(Tag, "итог: контейнер " + container + " весит " + new FileInfo(container).Length + " байт");

        // 9. Отмена длинного добавления. Приложения не может «не дописать»: после остановки
        // контейнер обязан остаться ровно таким, каким был до операции. Проверяем побайтово.
        string big = Path.Combine(root, "big");
        Directory.CreateDirectory(big);
        var rng = new Random(12345);
        for (int i = 0; i < 25; i++)
        {
            var bytes = new byte[4 * 1024 * 1024];
            rng.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(big, $"g{i:00}.bin"), bytes);
        }

        byte[] stampBefore = File.ReadAllBytes(container);
        int seen = 0;
        bool canceled = false;
        string cancelMessage = string.Empty;

        try
        {
            service.Append(container, [big], _ => { }, () => ++seen >= 6);
        }
        catch (System.OperationCanceledException ex)
        {
            canceled = true;
            cancelMessage = ex.Message;
        }

        Log.Info(Tag, $"попытка отмены: остановка после {seen} файлов, {cancelMessage}");
        if (!canceled)
            Fail(ref failures, "добавление 100 МБ не было прервано по shouldStop — отмена не работает");

        byte[] stampAfter = File.ReadAllBytes(container);
        if (stampAfter.Length != stampBefore.Length || !stampAfter.AsSpan().SequenceEqual(stampBefore.AsSpan()))
            Fail(ref failures, $"после отмены контейнер изменился: было {stampBefore.Length}, стало {stampAfter.Length}");
        else
            Log.Info(Tag, $"контейнер после отмены побайтово идентичен прежнему ({stampBefore.Length} байт)");

        return failures;
    }

    /// <summary>
    /// Открыть контейнер, которого приложение не создавало: он пришёл с Windows (CLI pack или
    /// WCX-плагин Total Commander). Если он читается, распаковывается деревом каталогов и проходит
    /// проверку чанков — формат совместим между платформами.
    /// </summary>
    private static void OpenOnly(string containerPath)
    {
        var service = new ContainerService();
        var (info, health) = service.Inspect(containerPath);

        Log.Info(Tag, $"открыт контейнер {containerPath}: версия {info.Version}, файлов {info.FileCount}, "
            + $"папок {info.FolderCount}, чанков {info.ChunkCount}, чанк {info.ChunkSize:N0} байт, уровень {info.CompressionLevel}");
        Log.Info(Tag, $"файл {health.FileSize:N0} байт, живых чанков {health.LiveChunkCount}, мёртвых {health.DeadChunkCount}, "
            + $"индекс {health.IndexBytes:N0}, мусора {health.ReclaimableBytes:N0}");

        LogTree(service, containerPath, "");

        string outDir = Path.Combine(StorageAccess.StartDirectory(), "Download", "open-out");
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, true);

        service.ExtractAll(containerPath, outDir, _ => { });

        var options = new EnumerationOptions { RecurseSubdirectories = true };
        int files = Directory.EnumerateFiles(outDir, "*", options).Count();
        int dirs = Directory.EnumerateDirectories(outDir, "*", options).Count();
        long bytes = Directory.EnumerateFiles(outDir, "*", options).Sum(f => new FileInfo(f).Length);

        Log.Info(Tag, $"распаковано на телефон: файлов {files}, папок {dirs}, байт {bytes:N0}");

        int verified = service.Verify(containerPath, [], _ => { }, () => false);
        Log.Info(Tag, $"проверка целостности: записей {verified}, расхождений нет");
    }

    private static void LogTree(ContainerService service, string container, string dir)
    {
        var rows = service.Children(container, dir);
        // Имена папок уже идут со «/» — это тот же вид, что в списке приложения.
        Log.Info(Tag, $"список {(dir.Length == 0 ? "корня" : dir)}: " + string.Join(", ", rows.Select(r => r.Name)));
    }

    private static long CompareTrees(string source, string restored)
    {
        long mismatches = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            string relative = Path.GetRelativePath(source, file);
            string other = Path.Combine(restored, relative);
            if (!File.Exists(other))
            {
                mismatches++;
                Log.Error(Tag, "нет после распаковки: " + relative);
                continue;
            }

            if (new FileInfo(file).Length != new FileInfo(other).Length)
            {
                mismatches++;
                Log.Error(Tag, $"размер не совпадает: {relative}");
                continue;
            }

            byte[] left = File.ReadAllBytes(file);
            byte[] right = File.ReadAllBytes(other);
            if (!left.AsSpan().SequenceEqual(right.AsSpan()))
            {
                mismatches++;
                Log.Error(Tag, "содержимое различается: " + relative);
            }
        }
        return mismatches;
    }

    private static byte[] Repeat(int value, int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(value + i % 251);
        return data;
    }

    private static void Fail(ref int failures, string message)
    {
        failures++;
        Log.Error(Tag, message);
    }
}
