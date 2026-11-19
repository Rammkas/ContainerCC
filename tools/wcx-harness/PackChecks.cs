using System.Security.Cryptography;

namespace WcxHarness;

/// <summary>
/// «Вставить в контейнер» и «удалить из контейнера» без Total Commander: PackFilesW/DeleteFilesW
/// и их ANSI-пара PackFiles/DeleteFiles.
///
/// TC передаёт сюда ровно то, что видит в панели: полный путь контейнера, SubPath (папка внутри
/// контейнера, в которую пользователь сейчас копирует), SrcPath (откуда копируем) и AddList — список
/// имён, разделённых нолями и оканчивающийся двумя нолями. Харнесс собирает такие же списки руками.
///
/// Всё делается на копии контейнера: исходный options.Container обязан остаться нетронутым для
/// проходов 1–13, которые идут раньше.
/// </summary>
internal static unsafe partial class Program
{
    private const int BigFileBytes = 3 << 20; // 3 МБ: при чанке 1 МБ это три чанка в новом контейнере

    // ─────────────────────────── проход 14: упаковка и удаление (Unicode) ───────────────────────────

    private static void RunPack(Plugin plugin, Options options)
    {
        Section("проход 14: упаковка (PackFilesW) и удаление (DeleteFilesW) — контейнер как обычная папка");

        if (!plugin.HasPackW)
        {
            Console.WriteLine($"  ПРОПУСК: из DLL не получены экспорты {Join(plugin.MissingPackExports)} — "
                + "плагин не умеет ни добавлять, ни удалять");
            Console.WriteLine();
            return;
        }

        string packDir = Path.Combine(options.WorkDir, "pack");
        Recreate(packDir);

        string srcDir = Path.Combine(packDir, "src");
        Directory.CreateDirectory(Path.Combine(srcDir, @"tree\sub"));
        Directory.CreateDirectory(Path.Combine(srcDir, @"tree\empty"));
        WriteText(Path.Combine(srcDir, "added.txt"), "Файл, добавленный в контейнер через PackFilesW.\r\n");
        WriteText(Path.Combine(srcDir, "добавлено-юникод.txt"), "Имя не ASCII: W-имена должны доходить без потерь.\r\n");
        WritePattern(Path.Combine(srcDir, "big.bin"), BigFileBytes);
        WritePattern(Path.Combine(srcDir, @"tree\sub\data.bin"), 300 << 10);
        WriteText(Path.Combine(srcDir, @"tree\sub\note.txt"), "внутри вложенной папки\r\n");

        Console.WriteLine($"  экспорты упаковки: PackFilesW {YesNo(plugin.HasPackW)}, DeleteFilesW {YesNo(plugin.HasDeleteW)}, "
            + $"PackFiles {YesNo(plugin.HasPackAnsi)}, DeleteFiles {YesNo(plugin.HasDeleteAnsi)}, "
            + $"PackSetDefaultParams {YesNo(plugin.HasPackDefaults)}");

        Check(sizeof(tPackDefaultParamStruct) == Wcx.SizeOfPackDefaults,
            $"sizeof(PackDefaultParamStruct) == {Wcx.SizeOfPackDefaults}, получено {sizeof(tPackDefaultParamStruct)}");

        // TC подсказывает пакерам общий ini (у TC это pkplugin.ini рядом с программой). Харнесс
        // подставляет путь в своей рабочей папке: тот же вид файла, но без риска что-то поменять
        // в реальной настройке TC.
        string settingsIni = Path.Combine(packDir, "pkplugin.ini");
        WriteText(settingsIni, "; общий ini пакеров: харнесс подставляет его так же, как Total Commander.\r\n");

        if (plugin.HasPackDefaults)
            ReportPackDefaults(plugin, settingsIni);

        // ── 1. новый контейнер (PK_CAPS_NEW): TC создаёт файл, которого ещё нет на диске
        //    Полоску прогресса TC показывает только если упаковщик зовёт tProcessDataProcW сам,
        //    поэтому зарегистрированный колбэк обязателен и при упаковке (handle здесь = 0).
        using Progress packProgress = new();
        Check(plugin.SetProcessDataProcW(0, packProgress.Handle) == 0,
            "SetProcessDataProcW(0, колбэк) перед PackFilesW вернул 0 (PackFiles handle архива не получает)");

        string fresh = Path.Combine(packDir, "fresh.cc");
        int code = PackW(plugin, fresh, null, srcDir,
            new[] { "added.txt", "добавлено-юникод.txt", "big.bin", "tree" }, 0);

        Check(code == 0, $"PackFilesW в несуществующий контейнер вернул {Wcx.ErrorName(code)}");
        Check(File.Exists(fresh), "новый контейнер создан на диске");

        ContainerModel freshModel = new();
        freshModel.MergeFrom(Snapshot.Collect(srcDir));

        CheckListing(ReadListing(plugin, fresh, verbose: false), freshModel, "список нового контейнера");
        Check(ExtractAllW(plugin, fresh, Path.Combine(packDir, "out-fresh")) == 0, "новый контейнер извлечён без ошибок");
        CompareModel(Path.Combine(packDir, "out-fresh"), freshModel, "содержимое нового контейнера");

        CheckProgress(packProgress.Calls, packProgress.Bytes, packProgress.Names, srcDir, "PackFilesW нового контейнера");

        // ── 2. добавление в существующий контейнер (PK_CAPS_MODIFY): копия исходного контейнера
        string container = Path.Combine(packDir, "modified.cc");
        File.Copy(options.Container, container, overwrite: true);

        Snapshot pristine = Snapshot.Collect(options.Source);
        ContainerModel model = new();
        model.MergeFrom(pristine);
        model.MergeFrom(Snapshot.Collect(srcDir));

        using Progress appendProgress = new();
        Check(plugin.SetProcessDataProcW(0, appendProgress.Handle) == 0,
            "колбэк прогресса зарегистрирован снова: после PackFilesW плагин его снимает");

        long beforeAppend = new FileInfo(container).Length;
        code = PackW(plugin, container, null, srcDir,
            new[] { "added.txt", "добавлено-юникод.txt", "big.bin", "tree" }, 0);
        Check(code == 0, $"PackFilesW в существующий контейнер вернул {Wcx.ErrorName(code)}");

        CheckProgress(appendProgress.Calls, appendProgress.Bytes, appendProgress.Names, srcDir,
            "PackFilesW в существующий контейнер");

        long afterAppend = new FileInfo(container).Length;
        Check(afterAppend > beforeAppend, $"размер контейнера вырос: {beforeAppend:N0} → {afterAppend:N0} байт");
        CheckListing(ReadListing(plugin, container, verbose: false), model, "список после добавления файлов и папки");

        // ── 3. то же имя = замена, как при копировании в обычную папку
        string replaceDir = Path.Combine(packDir, "replace");
        Directory.CreateDirectory(replaceDir);
        WriteText(Path.Combine(replaceDir, "added.txt"),
            "Вторая версия того же имени: запись должна замениться.\r\n" + new string('x', 4096) + "\r\n");

        code = PackW(plugin, container, null, replaceDir, new[] { "added.txt" }, 0);
        Check(code == 0, $"PackFilesW того же имени вернул {Wcx.ErrorName(code)}");
        model.AddFileFromDisk("added.txt", Path.Combine(replaceDir, "added.txt"));

        List<ArcEntry> listed = ReadListing(plugin, container, verbose: false);
        long expectedSize = new FileInfo(Path.Combine(replaceDir, "added.txt")).Length;
        ArcEntry? replaced = listed.FirstOrDefault(e => NameEquals(e.Name, "added.txt"));
        Check(replaced is not null && replaced.UnpSize == expectedSize,
            $"имя «added.txt» заменено, а не продублировано: в списке "
            + $"{replaced?.UnpSize.ToString() ?? "записи нет"} байт, ожидалось {expectedSize}");
        Check(listed.Count(e => NameEquals(e.Name, "added.txt")) == 1, "в списке ровно одна запись «added.txt»");
        CheckListing(listed, model, "список после замены имени");

        // ── 4. SubPath: вставка в подпапку контейнера (TC задаёт имя как «подпапка\имя»)
        code = PackW(plugin, container, "subdir", replaceDir, new[] { "added.txt" }, 0);
        Check(code == 0, $"PackFilesW с SubPath=[subdir] вернул {Wcx.ErrorName(code)}");
        model.AddFileFromDisk(@"subdir\added.txt", Path.Combine(replaceDir, "added.txt"));
        CheckListing(ReadListing(plugin, container, verbose: false), model, "список после вставки в SubPath");

        // ── 5. копирование внутрь пустой папки контейнера (запись пустой папки при этом исчезает)
        string emptyTarget = Path.Combine(packDir, "fresh-copy.cc");
        File.Copy(fresh, emptyTarget, overwrite: true);
        ContainerModel emptyModel = new();
        emptyModel.MergeFrom(Snapshot.Collect(srcDir));

        code = PackW(plugin, emptyTarget, @"tree\empty", replaceDir, new[] { "added.txt" }, 0);
        Check(code == 0, $"PackFilesW в пустую папку контейнера вернул {Wcx.ErrorName(code)}");
        emptyModel.AddFileFromDisk(@"tree\empty\added.txt", Path.Combine(replaceDir, "added.txt"));
        CheckListing(ReadListing(plugin, emptyTarget, verbose: false), emptyModel,
            "список после копирования в пустую папку: «tree\\empty» всё ещё видна");

        // ── 6. PK_PACK_MOVE_FILES: «переместить в контейнер» — оригиналы должны исчезнуть с диска
        string moveDir = Path.Combine(packDir, "move");
        Directory.CreateDirectory(moveDir);
        string movedFile = Path.Combine(moveDir, "doomed.txt");
        WriteText(movedFile, "Этот файл должен исчезнуть с диска после перемещения в контейнер.\r\n");

        ContainerModel movedModel = new();
        movedModel.AddFileFromDisk("doomed.txt", movedFile);

        string movedContainer = Path.Combine(packDir, "moved.cc");
        code = PackW(plugin, movedContainer, null, moveDir, new[] { "doomed.txt" }, Wcx.PK_PACK_MOVE_FILES);
        Check(code == 0, $"PackFilesW с PK_PACK_MOVE_FILES вернул {Wcx.ErrorName(code)}");
        Check(!File.Exists(movedFile), "оригинал удалён с диска (режим «переместить в контейнер»)");
        CheckListing(ReadListing(plugin, movedContainer, verbose: false), movedModel, "список после перемещения");

        // ── 7. удаление: файл и целиком папка, без распаковки контейнера
        string pristineFile = pristine.Files.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .First(k => k.Contains('\\') || pristine.Files.Count == 1);

        string? pristineDir = pristine.Directories
            .Where(d => pristine.Files.Keys.Any(f => NameUnder(d, f)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!plugin.HasDeleteW)
        {
            Console.WriteLine("  ПРОПУСК: DeleteFilesW не найден — проверки удаления не выполнялись");
        }
        else
        {
            List<string> toDelete = new() { pristineFile, @"tree\" };
            if (pristineDir is not null)
                toDelete.Add(pristineDir);

            long beforeDelete = new FileInfo(container).Length;
            code = DeleteW(plugin, container, toDelete.ToArray());
            Check(code == 0, $"DeleteFilesW {Quote(Join(toDelete))} вернул {Wcx.ErrorName(code)}");

            long afterDelete = new FileInfo(container).Length;
            Check(afterDelete >= afterAppend,
                $"после удаления размер файла {beforeDelete:N0} → {afterDelete:N0}: данные остаются до compact, "
                + "и об этом надо предупредить пользователя");

            model.Remove(pristineFile);
            model.Remove("tree");
            if (pristineDir is not null)
                model.Remove(pristineDir);

            List<ArcEntry> afterDeleteListing = ReadListing(plugin, container, verbose: false);
            CheckListing(afterDeleteListing, model, "список после удаления файла и папки");
            Check(!afterDeleteListing.Any(e => NameUnder("tree", e.Name)),
                "из списка исчезло всё содержимое удалённой папки «tree»");
            Check(!afterDeleteListing.Any(e => NameEquals(e.Name, pristineFile)),
                $"удалённый файл {Quote(pristineFile)} исчез из списка");
            Console.WriteLine($"  удалены: {Join(toDelete)}"
                + (pristineDir is null ? "" : " (папка целиком, вместе с содержимым)"));
        }

        // ── 8. удалён последний файл из папки: папка остаётся в контейнере пустой, как на диске
        CheckEmptiedFolders(plugin, fresh, srcDir, packDir);

        // ── 8.1. Del на папке: TC присылает маску её содержимого, а не имя папки
        CheckFolderMasks(plugin, packDir);

        // ── 9. пользователь нажал Stop: PackFiles возвращает E_EABORTED, контейнер не меняется
        string abortedCopy = Path.Combine(packDir, "aborted.cc");
        File.Copy(container, abortedCopy, overwrite: true);
        long lengthBeforeAbort = new FileInfo(abortedCopy).Length;

        using Progress stopped = new(abort: true);
        Check(plugin.SetProcessDataProcW(0, stopped.Handle) == 0, "колбэк, отвечающий «стоп», зарегистрирован");

        int abortCode = PackW(plugin, abortedCopy, null, replaceDir, new[] { "added.txt" }, 0);
        Check(abortCode == Wcx.E_EABORTED,
            $"PackFilesW при остановке пользователем вернул {Wcx.ErrorName(abortCode)}, ожидалось E_EABORTED");
        Check(stopped.Calls >= 1, $"плагин сообщил прогресс до остановки: вызовов {stopped.Calls}");
        CheckListing(ReadListing(plugin, abortedCopy, verbose: false), model,
            "после остановки контейнер читается ровно как до неё");
        Check(new FileInfo(abortedCopy).Length == lengthBeforeAbort,
            $"после остановки размер файла вернулся к прежнему (было {lengthBeforeAbort}, стало {new FileInfo(abortedCopy).Length})");

        string neverFinished = Path.Combine(packDir, "never-finished.cc");
        using Progress freshStopped = new(abort: true);
        Check(plugin.SetProcessDataProcW(0, freshStopped.Handle) == 0, "колбэк «стоп» зарегистрирован второй раз");

        int freshAbortCode = PackW(plugin, neverFinished, null, replaceDir, new[] { "added.txt" }, 0);
        Check(freshAbortCode == Wcx.E_EABORTED,
            $"PackFilesW нового контейнера при остановке вернул {Wcx.ErrorName(freshAbortCode)}");
        Check(!File.Exists(neverFinished), "недоделанный новый контейнер удалён с диска, а не оставлен битым");

        Console.WriteLine("  остановка пользователем: E_EABORTED, прежний контейнер цел, новый не оставлен неполным");

        // ── 10. всё оставшееся читается и извлекается побайтово        Check(VerifyAllW(plugin, container) == 0, "оставшееся содержимое контейнера проходит PK_TEST");
        Check(ExtractAllW(plugin, container, Path.Combine(packDir, "out-modified")) == 0,
            "изменённый контейнер извлечён без ошибок");
        CompareModel(Path.Combine(packDir, "out-modified"), model,
            "содержимое контейнера после добавлений и удалений");

        // ── 11. упаковка при открытой панели (handle списка ещё не закрыт)
        PackWhileListingOpen(plugin, container, replaceDir, model);

        // ── 12. отказы: то, что пользователь может сделать случайно
        CheckRefusals(plugin, container, srcDir, packDir);

        // ── 14.1 настройки пакера: ini из PackSetDefaultParams и диалог «Настроить»
        if (options.SkipSettings)
        {
            Section("проход 14.1: настройки пакера — ini из PackSetDefaultParams и диалог «Настроить»");
            Console.WriteLine("  ПРОПУСК: --skip-settings — диалог «Настроить» требует интерактивного рабочего стола");
            Console.WriteLine();
        }
        else
        {
            RunPackerSettings(plugin, options, settingsIni);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Последний файл папки удалён — папка обязана остаться в контейнере пустой: панель показывает
    /// каталоги по записям, а записей каталогов контейнер раньше не хранил.
    /// </summary>
    private static void CheckEmptiedFolders(Plugin plugin, string source, string srcDir, string packDir)
    {
        if (!plugin.HasDeleteW)
        {
            Console.WriteLine("  ПРОПУСК: проверка пустых папок после удаления требует DeleteFilesW");
            return;
        }

        string emptied = Path.Combine(packDir, "emptied.cc");
        File.Copy(source, emptied, overwrite: true);

        ContainerModel model = new();
        model.MergeFrom(Snapshot.Collect(srcDir));

        Check(DeleteW(plugin, emptied, new[] { @"tree\sub\note.txt" }) == 0,
            "DeleteFilesW одного из двух файлов папки «tree\\sub» вернул 0");
        model.Remove(@"tree\sub\note.txt");
        CheckListing(ReadListing(plugin, emptied, verbose: false), model,
            "после удаления одного файла папка «tree\\sub» на месте");

        Check(DeleteW(plugin, emptied, new[] { @"tree\sub\data.bin" }) == 0,
            "DeleteFilesW последнего файла папки «tree\\sub» вернул 0");
        model.Remove(@"tree\sub\data.bin");   // модель обязана оставить пустую папку

        List<ArcEntry> afterLast = ReadListing(plugin, emptied, verbose: false);
        CheckListing(afterLast, model, "папка «tree\\sub» осталась в списке пустой: удаляли файл, а не папку");
        Check(afterLast.Any(e => NameEquals(e.Name, @"tree\sub")), "«tree\\sub» по-прежнему видна в списке");

        string emptiedOut = Path.Combine(packDir, "out-emptied");
        Check(ExtractAllW(plugin, emptied, emptiedOut) == 0, "контейнер с пустой папкой извлечён без ошибок");
        Check(Directory.Exists(Path.Combine(emptiedOut, @"tree\sub")), "пустая папка создана и при извлечении");
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(emptiedOut, @"tree\sub")).Any(),
            "извлечённая «tree\\sub» пуста");
        CompareModel(emptiedOut, model, "содержимое контейнера после удаления последнего файла из папки");

        Check(DeleteW(plugin, emptied, new[] { "tree\\sub" }) == 0, "DeleteFilesW пустой папки по имени вернул 0");
        model.Remove("tree\\sub");
        CheckListing(ReadListing(plugin, emptied, verbose: false), model,
            "пустая папка удалена по имени и не появилась снова");

        Console.WriteLine("  удалён последний файл папки: она остаётся пустой; папку, удалённую по имени, плагин не возрождает");
    }

    /// <summary>
    /// Del на папке внутри контейнера: TC передаёт не имя папки, а маску её содержимого —
    /// «bin\tools\*.*». Пока упаковщик принимал только имена, такое удаление возвращало
    /// E_NOT_SUPPORTED, и удалить папку внутри контейнера было нельзя.
    /// </summary>
    private static void CheckFolderMasks(Plugin plugin, string packDir)
    {
        Console.WriteLine();
        Console.WriteLine("  удаление папки маской (именно так TC передаёт Del на папке)");

        if (!plugin.HasDeleteW)
        {
            Console.WriteLine("  ПРОПУСК: проверка масок удаления требует DeleteFilesW");
            return;
        }

        string maskDir = Path.Combine(packDir, "masks");
        Recreate(maskDir);

        string srcDir = Path.Combine(maskDir, "src");
        Directory.CreateDirectory(Path.Combine(srcDir, @"bin\tools"));
        Directory.CreateDirectory(Path.Combine(srcDir, "keep"));
        Directory.CreateDirectory(Path.Combine(srcDir, "пустая"));
        WriteText(Path.Combine(srcDir, "отчёт.txt"), "файл в корне контейнера\r\n");
        WriteText(Path.Combine(srcDir, "запись.log"), "файл в корне контейнера\r\n");
        WriteText(Path.Combine(srcDir, @"bin\app.txt"), "файл папки bin\r\n");
        WriteText(Path.Combine(srcDir, @"bin\notes.log"), "файл папки bin\r\n");
        WriteText(Path.Combine(srcDir, @"bin\tools\helper.txt"), "файл папки bin\\tools\r\n");
        WriteText(Path.Combine(srcDir, @"bin\tools\helper.log"), "файл папки bin\\tools\r\n");
        WriteText(Path.Combine(srcDir, @"keep\hold.txt"), "это должно остаться\r\n");

        string container = Path.Combine(maskDir, "masks.cc");

        using Progress progress = new();
        Check(plugin.SetProcessDataProcW(0, progress.Handle) == 0,
            "SetProcessDataProcW(0, колбэк) перед упаковкой контейнера масок вернул 0");
        Check(PackW(plugin, container, null, srcDir, new[] { "отчёт.txt", "запись.log", "bin", "keep", "пустая" }, 0) == 0,
            "PackFilesW контейнера для проверки масок вернул 0");

        ContainerModel model = new();
        model.MergeFrom(Snapshot.Collect(srcDir));
        CheckListing(ReadListing(plugin, container, verbose: false), model, "исходный список контейнера для масок");

        // 1. Del на папке «bin\tools»: в DeleteList приходит «bin\tools\*.*».
        Check(DeleteW(plugin, container, new[] { @"bin\tools\*.*" }) == 0,
            "DeleteFilesW с маской «bin\\tools\\*.*» (так TC передаёт Del на папке) вернул 0");
        model.Remove(@"bin\tools");
        CheckListing(ReadListing(plugin, container, verbose: false), model,
            "маска «bin\\tools\\*.*» убрала папку bin\\tools целиком, «bin» осталась на месте");

        // 2. «?» — ровно один символ; маска не перепрыгивает через разделитель.
        Check(DeleteW(plugin, container, new[] { @"bin\app.tx?" }) == 0,
            "DeleteFilesW с маской «bin\\app.tx?» вернул 0");
        model.Remove(@"bin\app.txt");
        CheckListing(ReadListing(plugin, container, verbose: false), model, "маска с «?» убрала ровно один файл");

        // 3. маска имён внутри папки: файлы убраны, сама папка остаётся — её удаляли не по имени.
        Check(DeleteW(plugin, container, new[] { @"bin\*.log" }) == 0,
            "DeleteFilesW с маской «bin\\*.log» вернул 0");
        model.Remove(@"bin\notes.log");
        List<ArcEntry> afterBinMask = ReadListing(plugin, container, verbose: false);
        CheckListing(afterBinMask, model, "маска по файлам папки: «bin» осталась в списке пустой");
        Check(afterBinMask.Any(e => NameEquals(e.Name, "bin")), "освободившаяся папка «bin» видна в списке");

        // 4. маска, под которую ничего не попало: «нет файлов для удаления», а не «плагин не умеет».
        int nothing = DeleteW(plugin, container, new[] { @"bin\*.nosuch" });
        Check(nothing == Wcx.E_NO_FILES,
            $"DeleteFilesW с маской «bin\\*.nosuch» вернул {Wcx.ErrorName(nothing)}, ожидалось E_NO_FILES");

        // 5. маска не даёт выбраться из контейнера.
        long sizeBeforeEscape = new FileInfo(container).Length;
        int escaped = DeleteW(plugin, container, new[] { @"..\вне\*.*" });
        Check(escaped == Wcx.E_NOT_SUPPORTED,
            $"DeleteFilesW с маской [..\\вне\\*.*] вернул {Wcx.ErrorName(escaped)}, ожидалось E_NOT_SUPPORTED");
        Check(new FileInfo(container).Length == sizeBeforeEscape, "отказ из-за «..» не изменил контейнер");
        CheckListing(ReadListing(plugin, container, verbose: false), model, "после отказа контейнер не изменился");

        // 6. маска и обычные имена в одном вызове.
        Check(DeleteW(plugin, container, new[] { @"пустая\*.*", "запись.log", @"keep\hold.txt" }) == 0,
            "DeleteFilesW со смесью маски и имён вернул 0");
        model.Remove("пустая");
        model.Remove("запись.log");
        model.Remove(@"keep\hold.txt");
        CheckListing(ReadListing(plugin, container, verbose: false), model,
            "маска пустой папки и два имени удалены за один вызов; «keep» осталась пустой");

        string outDir = Path.Combine(maskDir, "out");
        Check(ExtractAllW(plugin, container, outDir) == 0, "контейнер после удалений маской извлечён без ошибок");
        Check(!Directory.Exists(Path.Combine(outDir, @"bin\tools")), "извлечённая папка «bin\\tools» не появилась снова");
        CompareModel(outDir, model, "содержимое контейнера после удалений маской");

        // 7. «*.*» из пустой панели: удаляется всё содержимое контейнера, вместе с пустыми папками.
        Check(DeleteW(plugin, container, new[] { @"*.*" }) == 0,
            "DeleteFilesW с корневой маской «*.*» вернул 0");
        List<ArcEntry> emptiedAll = ReadListing(plugin, container, verbose: false);
        Check(emptiedAll.Count == 0, $"после «*.*» в списке записей {emptiedAll.Count}, ожидалось 0");

        Console.WriteLine("  «bin\\tools\\*.*» = Del на папке; «bin\\*.log» = только файлы папки; «*.*» = всё содержимое контейнера");
    }

    /// <summary>TC держит открытым handle панели, и в эту же копию контейнера тем временем копируют файлы.</summary>
    private static void PackWhileListingOpen(Plugin plugin, string container, string srcDir, ContainerModel model)
    {
        Console.WriteLine();
        Console.WriteLine("  упаковка при открытой панели (OpenArchiveW ещё не закрыт)");

        fixed (char* archiveName = container)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW для теста «панель открыта» вернул 0 (OpenResult = {Wcx.ErrorName(data.OpenResult)})");
                return;
            }

            // Перед PackFiles TC вызывает SetProcessDataProcW с handle = 0: у упаковки handle нет.
            using Progress progress = new();
            int registered = plugin.SetProcessDataProcW(0, progress.Handle);
            Check(registered == 0, $"SetProcessDataProcW(0, proc) перед упаковкой вернул {Wcx.ErrorName(registered)}");

            int code = PackW(plugin, container, null, srcDir, new[] { "added.txt" }, 0);
            Check(code == 0, $"PackFilesW при открытом handle вернул {Wcx.ErrorName(code)}");

            Check(plugin.CloseArchive(handle) == 0, "CloseArchive после упаковки вернул 0");

            model.AddFileFromDisk("added.txt", Path.Combine(srcDir, "added.txt"));
            CheckListing(ReadListing(plugin, container, verbose: false), model,
                "список после упаковки при открытой панели: новая запись видна");
        }
    }

    private static void CheckRefusals(Plugin plugin, string container, string srcDir, string packDir)
    {
        Console.WriteLine();
        Console.WriteLine("  отказы (то, что пользователь может сделать случайно)");

        long untouched = new FileInfo(container).Length;

        if (plugin.HasDeleteW)
        {
            int missing = DeleteW(plugin, container, new[] { "такого-файла-нет.txt" });
            Check(missing == Wcx.E_NO_FILES,
                $"DeleteFilesW несуществующего имени вернул {Wcx.ErrorName(missing)}, ожидалось E_NO_FILES");
        }

        int empty = PackW(plugin, container, null, srcDir, Array.Empty<string>(), 0);
        Check(empty == Wcx.E_NO_FILES,
            $"PackFilesW с пустым AddList вернул {Wcx.ErrorName(empty)}, ожидалось E_NO_FILES");

        int encrypted = PackW(plugin, container, null, srcDir, new[] { "added.txt" }, Wcx.PK_PACK_ENCRYPT);
        Check(encrypted == Wcx.E_NOT_SUPPORTED,
            $"PackFilesW с PK_PACK_ENCRYPT вернул {Wcx.ErrorName(encrypted)}, ожидалось E_NOT_SUPPORTED "
            + "(шифрования в формате нет; молча положить нешифрованные данные хуже отказа)");

        int escaped = PackW(plugin, container, @"..\evil", srcDir, new[] { "added.txt" }, 0);
        Check(escaped == Wcx.E_NOT_SUPPORTED,
            $"PackFilesW с SubPath=[..\\evil] вернул {Wcx.ErrorName(escaped)}, ожидалось E_NOT_SUPPORTED");

        int badName = PackW(plugin, container, null, srcDir, new[] { @"ok\..\..\escape.txt" }, 0);
        Check(badName == Wcx.E_NOT_SUPPORTED,
            $"PackFilesW с именем [ok\\..\\..\\escape.txt] вернул {Wcx.ErrorName(badName)}, ожидалось E_NOT_SUPPORTED");

        Check(new FileInfo(container).Length == untouched,
            "ни одна отказанная операция не изменила контейнер: индекс меняется одним последним блоком");

        int missingSource = PackW(plugin, container, null, srcDir, new[] { "net-takogo-fayla.txt" }, 0);
        Check(missingSource == Wcx.E_EOPEN,
            $"PackFilesW отсутствующего на диске файла вернул {Wcx.ErrorName(missingSource)}, ожидалось E_EOPEN");

        string nowhere = Path.Combine(packDir, "net-takoy-papki", "x.cc");
        int nowhereCode = PackW(plugin, nowhere, null, srcDir, new[] { "added.txt" }, 0);
        Check(nowhereCode == Wcx.E_EOPEN,
            $"PackFilesW в контейнер внутри несуществующей папки вернул {Wcx.ErrorName(nowhereCode)}, ожидалось E_EOPEN");

        if (plugin.HasDeleteW)
        {
            int absent = DeleteW(plugin, Path.Combine(packDir, "net-takogo-konteynera.cc"), new[] { "a.txt" });
            Check(absent == Wcx.E_EOPEN,
                $"DeleteFilesW отсутствующего контейнера вернул {Wcx.ErrorName(absent)}, ожидалось E_EOPEN");
        }
    }

    // ─────────────────────────── проход 15: ANSI-ветка упаковки ───────────────────────────

    /// <summary>
    /// 32-битный TC (и любой клиент «первого издания») зовёт PackFiles/DeleteFiles без суффикса W,
    /// а имена там — байты в кодовой странице системы. Проверяем, что запись, положенную через ANSI,
    /// читает юникод-ветка, причём дословно, включая кириллицу: у плагина и у ядра одно правило имён.
    /// Имена берём через CodePageNames.Pick: на CP_ACP = 1251 это кириллица (как и прежде), на 1252
    /// (CI-машина) — латиница с диакритикой. Непредставимое имя хост превратил бы в «?», и проход
    /// 15 сравнивал бы уже не плагин, а кодовую страницу машины.
    /// </summary>
    private static void RunPackAnsi(Plugin plugin, Options options)
    {
        Section("проход 15: ANSI-упаковка (PackFiles/DeleteFiles) и чтение юникод-веткой");

        if (!plugin.HasAnsiBranch)
        {
            Console.WriteLine("  ПРОПУСК: ANSI-ветка не загружена (см. проходы 7–13)");
            Console.WriteLine();
            return;
        }

        if (!plugin.HasPackAnsi || !plugin.HasDeleteAnsi)
        {
            Console.WriteLine($"  ПРОПУСК: экспорты {Join(plugin.MissingPackExports)} не найдены");
            Console.WriteLine();
            return;
        }

        string ansiDir = Path.Combine(options.AnsiWorkDir, "pack");
        Recreate(ansiDir);

        string srcDir = Path.Combine(ansiDir, "src");
        string folderName = CodePageNames.Pick("ansi-папка", "ansi-pápka", "ansi-papka");
        string insideName = CodePageNames.Pick("отчёт.txt", "otchyót.txt", "otchet.txt");

        Directory.CreateDirectory(Path.Combine(srcDir, folderName));
        WriteText(Path.Combine(srcDir, "ansi.txt"), "запись, положенная через ANSI PackFiles\r\n");
        WriteText(Path.Combine(srcDir, folderName, insideName), "имя в кодовой странице системы\r\n");

        string container = Path.Combine(ansiDir, "ansi-pack.cc");

        using AnsiProgress ansiProgress = new();
        Check(plugin.SetProcessDataProc(0, ansiProgress.Handle) == 0,
            "SetProcessDataProc(0, колбэк) перед PackFiles (ANSI) вернул 0");

        int code = PackA(plugin, container, null, srcDir, new[] { "ansi.txt", folderName }, 0);
        Check(code == 0, $"PackFiles (ANSI) вернул {Wcx.ErrorName(code)}");

        ContainerModel model = new();
        model.MergeFrom(Snapshot.Collect(srcDir));

        long sourceBytes = model.Files.Values.Sum(file => file.Size);
        Check(ansiProgress.Calls == model.FileCount,
            $"PackFiles (ANSI): колбэк прогресса вызван {ansiProgress.Calls} раз, ожидалось {model.FileCount}");
        Check(ansiProgress.Bytes == sourceBytes,
            $"PackFiles (ANSI): колбэк получил {ansiProgress.Bytes:N0} байт, ожидалось {sourceBytes:N0}");
        Check(ansiProgress.Names.All(name => name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)),
            "PackFiles (ANSI): имена прогресса — файлы источника в кодовой странице системы");

        // Список читаем W-веткой: то, что записал ANSI-вызов, должна показывать панель 64-битного TC.
        CheckListing(ReadListing(plugin, container, verbose: false), model,
            "юникод-список контейнера, созданного ANSI-упаковкой (имена совпадают дословно)");

        Check(ExtractAllW(plugin, container, Path.Combine(ansiDir, "out")) == 0, "ANSI-записи извлечены W-веткой");
        CompareModel(Path.Combine(ansiDir, "out"), model, "содержимое ANSI-контейнера");

        // Del на папке приходит маской её содержимого и в ANSI-ветке: «<папка>\*.*».
        string maskCopy = Path.Combine(ansiDir, "ansi-mask.cc");
        File.Copy(container, maskCopy, overwrite: true);
        int maskCode = DeleteA(plugin, maskCopy, new[] { $@"{folderName}\*.*" });
        Check(maskCode == 0, $"DeleteFiles (ANSI) с маской «{folderName}\\*.*» вернул {Wcx.ErrorName(maskCode)}");
        ContainerModel maskModel = new();
        maskModel.MergeFrom(Snapshot.Collect(srcDir));
        maskModel.Remove(folderName);
        CheckListing(ReadListing(plugin, maskCopy, verbose: false), maskModel,
            $"ANSI-контейнер после удаления папки маской: «{folderName}» исчезла вместе с содержимым");

        int deleteCode = DeleteA(plugin, container, new[] { folderName });
        Check(deleteCode == 0, $"DeleteFiles (ANSI) по имени папки вернул {Wcx.ErrorName(deleteCode)}");
        model.Remove(folderName);
        CheckListing(ReadListing(plugin, container, verbose: false), model, "список после ANSI-удаления папки");

        // Остановка из ANSI-колбэка: PackFiles возвращает E_EABORTED, прежний контейнер остаётся целым.
        string aborted = Path.Combine(ansiDir, "ansi-abort.cc");
        File.Copy(container, aborted, overwrite: true);

        using AnsiProgress ansiStopped = new(abort: true);
        Check(plugin.SetProcessDataProc(0, ansiStopped.Handle) == 0, "ANSI-колбэк, отвечающий «стоп», зарегистрирован");

        int abortCode = PackA(plugin, aborted, null, srcDir, new[] { "ansi.txt" }, 0);
        Check(abortCode == Wcx.E_EABORTED,
            $"PackFiles (ANSI) при остановке пользователем вернул {Wcx.ErrorName(abortCode)}, ожидалось E_EABORTED");
        Check(ansiStopped.Calls >= 1, $"ANSI-упаковка сообщила прогресс до остановки: вызовов {ansiStopped.Calls}");
        CheckListing(ReadListing(plugin, aborted, verbose: false), model,
            "ANSI-контейнер после остановки читается ровно как до неё");

        int refused = PackA(plugin, container, null, srcDir, new[] { @"..\outside.txt" }, 0);
        Check(refused == Wcx.E_NOT_SUPPORTED,
            $"PackFiles (ANSI) с именем [..\\outside.txt] вернул {Wcx.ErrorName(refused)}, ожидалось E_NOT_SUPPORTED");

        Console.WriteLine();
    }

    // ─────────────────── проход 16: имена вне кодовой страницы системы ───────────────────

    /// <summary>
    /// ANSI-ветка получает имена байтами кодовой страницы системы, и символов, которых в ней нет,
    /// хост уже не донесёт: Win32 заменяет их знаком «?». Проба — имя с U+4E2D: символа нет ни в 1251
    /// (домашняя CP_ACP), ни в 1252 (CP_ACP CI-раннера), поэтому на обеих машинах хост приносит «?»
    /// и проверяется поведение плагина, а не удачное совпадение кодовых страниц. π для пробы не
    /// годится: без WC_NO_BEST_FIT_CHARS в 1252 он best-fit сворачивается в «p», и имя стало бы
    /// законным. Проверяем, что в ANSI-списке имя показано как проекция кодовой страницы, PK_TEST
    /// проходит (на диск не пишет), PK_EXTRACT возвращает E_EWRITE (диск «?» не примет), ANSI-
    /// упаковка такого имени отказывается до записи, а юникод-ветка видит символ дословно и работает
    /// без единой ошибки. Ожидания выводятся из фактической проекции имени, а не из номера кодовой
    /// страницы: там, где страница символ вмещает (UTF-8, 936), те же проверки ждут успеха.
    /// </summary>
    private static void RunAnsiCodePageQuirk(Plugin plugin, Options options)
    {
        Section("проход 16: имена вне кодовой страницы системы — ANSI-ветка и символ, ставший «?»");

        if (!plugin.HasAnsiBranch || !plugin.HasPackW || !plugin.HasPackAnsi)
        {
            Console.WriteLine("  ПРОПУСК: нужны ANSI-ветка и обе ветки PackFiles (см. проходы 7–13 и 15)");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"  CP_ACP = {AnsiCodePage.CurrentCodePage()}; имя-проба содержит U+4E2D, которого нет "
            + "ни в 1251, ни в 1252 — хост превращает его в «?», и проверяем мы теперь именно это поведение, "
            + "а не удачное совпадение кодовых страниц");

        string dir = Path.Combine(options.AnsiWorkDir, "codepage");
        Recreate(dir);

        string srcDir = Path.Combine(dir, "src");
        Directory.CreateDirectory(srcDir);

        // Символ написан побегом, чтобы имя пробы не зависело от того, как данный редактор хранит файл.
        const string oddName = "probe-\u4E2D.txt";
        const string okName = "probe-ok.txt";
        WriteText(Path.Combine(srcDir, oddName), "имя с символом вне кодовой страницы системы\r\n");
        WriteText(Path.Combine(srcDir, okName), "обычное имя, которое кодовая страница умеет\r\n");

        string oddProjected = AnsiCodePage.Project(oddName);
        bool projectedIsLegal = oddProjected.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        Console.WriteLine($"  {Quote(oddName)} → в CP_ACP это {Quote(oddProjected)}"
            + (oddProjected == oddName ? " (представимо)" : projectedIsLegal
                ? " (свёрнуто best-fit в другой допустимый символ)" : " (не представимо)"));
        Console.WriteLine("  кодовая страница сворачивает U+4E2D в «?»: ANSI-ветка ждёт отказов там, где юникод-ветка работает");

        ContainerModel model = new();
        model.MergeFrom(Snapshot.Collect(srcDir));

        // Best-fit-побег: если страница заменила символ другим допустимым символом, имя-проекция
        // появляется источником, чтобы проверка ANSI-упаковки не свалилась в «нет такого файла».
        if (projectedIsLegal && oddProjected != oddName)
            WriteText(Path.Combine(srcDir, oddProjected), "имя, в которое сворачивает кодовую страницу пробное имя\r\n");

        // Кладём через юникод-ветку: на диске и внутри контейнера символ остаётся символом.
        string container = Path.Combine(dir, "codepage.cc");
        Check(PackW(plugin, container, null, srcDir, new[] { oddName, okName }, 0) == 0,
            "PackFilesW положил в контейнер запись, имя которой ANSI-ветка выразить не сможет");
        CheckListing(ReadListing(plugin, container, verbose: false), model,
            "юникод-список: имя с символом показано дословно, кодовая страница помехой не стала");
        Check(VerifyAllW(plugin, container) == 0, "PK_TEST (W) читает запись с символом вне кодовой страницы без ошибок");
        Check(ExtractAllW(plugin, container, Path.Combine(dir, "out-w")) == 0,
            "PK_EXTRACT (W) извлёк обе записи без единой ошибки");
        Check(File.Exists(Path.Combine(dir, "out-w", oddName)),
            $"файл {Quote(oddName)} лёг на диск юникод-веткой — трудность только в ANSI-отображении имени");

        // Теперь ANSI: список показывает проекцию, проверка проходит, извлечение спотыкается о «?» или пишет проекцию.
        Check(VerifyAllA(plugin, container) == 0,
            "PK_TEST (ANSI) прошёл по записи с «?» без ошибок: проверка ничего не пишет на диск");

        string outA = Path.Combine(dir, "out-a");
        List<(string Name, int Code)> extractedA = ExtractAllA(plugin, container, outA);
        Check(extractedA.Count == model.Files.Count,
            $"ANSI-цикл прочитал {extractedA.Count} записей, ожидалось {model.Files.Count}");

        (string oddActual, int oddCode) = extractedA.FirstOrDefault(entry => NameEquals(entry.Name, oddProjected));
        Check(oddActual.Length > 0,
            $"ANSI-листинг показал {Quote(oddName)} как его проекцию {Quote(oddProjected)} "
            + $"(в списке: {Join(extractedA.Select(entry => Quote(entry.Name)))})");

        string[] expectedOnDisk = projectedIsLegal ? [okName, oddProjected] : [okName];
        if (!projectedIsLegal)
        {
            Check(oddCode == Wcx.E_EWRITE,
                $"ANSI PK_EXTRACT имени с «?» вернул {Wcx.ErrorName(oddCode)}, ожидался E_EWRITE: "
                + "плагин не делает вид, что записал файл, которого диск создать не дал");
        }
        else
        {
            Check(oddCode == 0,
                $"ANSI PK_EXTRACT имени {Quote(oddProjected)} вернул {Wcx.ErrorName(oddCode)}: "
                + "кодовая страница выразила имя допустимо, и извлечение удалось");
        }

        Check(extractedA.Any(entry => NameEquals(entry.Name, okName) && entry.Code == 0)
            && File.Exists(Path.Combine(outA, okName)),
            $"обычная запись {Quote(okName)} извлечена ANSI-веткой без ошибок — сбой касается только проблемного имени");

        List<string> leftOnDisk = Directory.Exists(outA)
            ? Directory.GetFiles(outA).Select(path => Path.GetFileName(path) ?? string.Empty).ToList()
            : new List<string>();
        Check(leftOnDisk.Count == expectedOnDisk.Length
                && expectedOnDisk.All(name => leftOnDisk.Any(left => NameEquals(left, name))),
            $"ANSI-извлечение оставило на диске {Join(leftOnDisk)}, ожидалось {Join(expectedOnDisk)}: "
            + "«?»-имя не появилось и не было подменено другим");

        // Та же проекция, пришедшая со стороны хоста, не должна попасть в контейнер, если имя с «?».
        string oddContainer = Path.Combine(dir, "odd-pack.cc");
        int packCode = PackA(plugin, oddContainer, null, srcDir, new[] { oddProjected }, 0);
        if (!projectedIsLegal)
        {
            Check(packCode == Wcx.E_NOT_SUPPORTED,
                $"ANSI PackFiles имени {Quote(oddProjected)} вернул {Wcx.ErrorName(packCode)}, "
                + "ожидался E_NOT_SUPPORTED: имя с «?» в контейнер кладётся только честным именем источника");
            Check(!File.Exists(oddContainer), "отказ произошёл до записи — частичного контейнера нет");
        }
        else
        {
            Check(packCode == 0,
                $"ANSI PackFiles имени {Quote(oddProjected)} вернул {Wcx.ErrorName(packCode)}: кодовая страница "
                + "имя вмещает, и отказывал бы плагин только по настоящему поводу");
            Check(File.Exists(oddContainer)
                    && ReadListing(plugin, oddContainer, verbose: false).Any(entry => NameEquals(entry.Name, oddProjected)),
                $"ANSI PackFiles создал контейнер с записью {Quote(oddProjected)} — ветка не теряет имена по дороге");
        }

        CheckListing(ReadListing(plugin, container, verbose: false), model,
            "контейнер после отказов и неудачного извлечения не изменился ни на запись");

        Console.WriteLine();
    }

    // ─────────────────────────── вызовы упаковщика ───────────────────────────

    /// <summary>
    /// Полоску прогресса TC рисует только тогда, когда упаковщик сам зовёт tProcessDataProc:
    /// проверяем число вызовов, суммарный объём и то, что плагин называет файлы источника.
    /// </summary>
    private static void CheckProgress(int calls, long bytes, List<string> names, string srcDir, string what)
    {
        List<string> sources = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories).ToList();
        long expectedBytes = sources.Sum(path => new FileInfo(path).Length);

        Check(calls == sources.Count,
            $"{what}: колбэк прогресса вызван {calls} раз, ожидалось {sources.Count} (по одному сообщению на файл)");
        Check(bytes == expectedBytes,
            $"{what}: колбэк получил {bytes:N0} байт, ожидалось {expectedBytes:N0} (сумма размеров файлов источника)");
        Check(names.Count == calls && names.All(name => name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)),
            $"{what}: имена прогресса — файлы источника, которые TC и так показывает в диалоге копирования");
    }

    /// <summary>
    /// TC вызывает PackSetDefaultParams сразу после загрузки DLL и больше к ней не обращается:
    /// больше пакер путь к своему ini не получает, поэтому именно здесь решается, куда плагин
    /// пишет настройки. Харнесс даёт свой файл и потом проверяет их проход 14.1.
    /// </summary>
    private static void ReportPackDefaults(Plugin plugin, string settingsIni)
    {
        tPackDefaultParamStruct defaults = default;
        defaults.size = Wcx.SizeOfPackDefaults;
        defaults.PluginInterfaceVersionLow = 0x0000_0002;
        defaults.PluginInterfaceVersionHi = 0x0000_0015; // 2.21

        byte[] ini = AnsiCodePage.ToAnsiZ(settingsIni);
        fixed (byte* iniPointer = ini)
        {
            // fixed-size buffer уже «закреплён»: его адрес берут напрямую, без fixed.
            byte* target = defaults.DefaultIniName;
            for (int i = 0; i < ini.Length && i < 260; i++)
                target[i] = iniPointer[i];

            plugin.PackSetDefaultParams(&defaults);
        }

        Console.WriteLine($"  PackSetDefaultParams принята: size {defaults.size}, интерфейс 2.21, "
            + $"ini пакеров передан: {Quote(settingsIni)}");
    }

    private static int PackW(Plugin plugin, string container, string? subPath, string? srcPath, string[] names, int flags)
    {
        char[] list = ZListW(names);

        fixed (char* packedFile = container)
        fixed (char* sub = subPath)
        fixed (char* src = srcPath)
        fixed (char* addList = list)
            return plugin.PackFilesW(packedFile, sub, src, addList, flags);
    }

    private static int DeleteW(Plugin plugin, string container, string[] names)
    {
        char[] list = ZListW(names);

        fixed (char* packedFile = container)
        fixed (char* deleteList = list)
            return plugin.DeleteFilesW(packedFile, deleteList);
    }

    private static int PackA(Plugin plugin, string container, string? subPath, string? srcPath, string[] names, int flags)
    {
        byte[] packed = AnsiCodePage.ToAnsiZ(container);
        byte[] sub = subPath is null ? Array.Empty<byte>() : AnsiCodePage.ToAnsiZ(subPath);
        byte[] src = srcPath is null ? Array.Empty<byte>() : AnsiCodePage.ToAnsiZ(srcPath);
        byte[] list = ZListA(names);

        fixed (byte* packedFile = packed)
        fixed (byte* subPointer = sub)
        fixed (byte* srcPointer = src)
        fixed (byte* addList = list)
            return plugin.PackFiles(packedFile, subPointer, srcPointer, addList, flags);
    }

    private static int DeleteA(Plugin plugin, string container, string[] names)
    {
        byte[] packed = AnsiCodePage.ToAnsiZ(container);
        byte[] list = ZListA(names);

        fixed (byte* packedFile = packed)
        fixed (byte* deleteList = list)
            return plugin.DeleteFiles(packedFile, deleteList);
    }

    /// <summary>AddList и DeleteList: имена подряд, каждое закончено нолем, весь список — двумя нолями.</summary>
    private static char[] ZListW(string[] names)
    {
        var list = new List<char>();
        foreach (string name in names)
        {
            foreach (char c in name)
                list.Add(c);

            list.Add('\0');
        }

        list.Add('\0');
        return list.ToArray();
    }

    private static byte[] ZListA(string[] names)
    {
        var list = new List<byte>();
        foreach (string name in names)
        {
            list.AddRange(AnsiCodePage.ToAnsi(name));
            list.Add(0);
        }

        list.Add(0);
        return list.ToArray();
    }

    // ─────────────────────────── цикл «как это делает TC» ───────────────────────────

    private static int ExtractAllW(Plugin plugin, string container, string target)
    {
        Recreate(target);
        int errors = 0;

        fixed (char* archiveName = container)
        fixed (char* targetPath = target)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW(PK_OM_EXTRACT) для {Quote(container)} вернул 0 "
                    + $"(OpenResult = {Wcx.ErrorName(data.OpenResult)})");
                return -1;
            }

            while (true)
            {
                tHeaderDataExW header = default;
                int code = plugin.ReadHeaderExW(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ReadHeaderExW в {Quote(container)} вернул {Wcx.ErrorName(code)}");
                    errors++;
                    break;
                }

                byte* raw = (byte*)&header;
                string name = ReadUtf16(raw + Wcx.OffFileName, 1024);

                fixed (char* entryName = name)
                    if (plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, targetPath, entryName) != 0)
                        errors++;
            }

            plugin.CloseArchive(handle);
        }

        return errors;
    }

    private static int VerifyAllW(Plugin plugin, string container)
    {
        int errors = 0;

        fixed (char* archiveName = container)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW для PK_TEST вернул 0 для {Quote(container)}");
                return -1;
            }

            while (true)
            {
                tHeaderDataExW header = default;
                int code = plugin.ReadHeaderExW(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ReadHeaderExW в {Quote(container)} вернул {Wcx.ErrorName(code)}");
                    errors++;
                    break;
                }

                if (plugin.ProcessFileW(handle, Wcx.PK_TEST, null, null) != 0)
                    errors++;
            }

            plugin.CloseArchive(handle);
        }

        return errors;
    }

    /// <summary>
    /// ANSI-близнец ExtractAllW: те же вызовы без суффикса W, имена читаются из CP_ACP и передаются
    /// обратно как есть. Возвращает список (имя из ANSI-листинга, код ProcessFile) — проход 16
    /// сверяет и имя, и код: у непредставимого имени кода это отдельная проверка.
    /// </summary>
    private static List<(string Name, int Code)> ExtractAllA(Plugin plugin, string container, string target)
    {
        var results = new List<(string Name, int Code)>();
        Recreate(target);

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(container);
        byte[] ansiTarget = AnsiCodePage.ToAnsiZ(target);
        byte[] comment = new byte[4096];

        fixed (byte* archiveName = ansiArchive)
        fixed (byte* commentBuffer = comment)
        fixed (byte* targetPath = ansiTarget)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive(PK_OM_EXTRACT) для {Quote(container)} вернул 0 "
                    + $"(OpenResult = {Wcx.ErrorName(data.OpenResult)})");
                return results;
            }

            while (true)
            {
                tHeaderDataEx header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderEx(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ANSI ReadHeaderEx в {Quote(container)} вернул {Wcx.ErrorName(code)}");
                    break;
                }

                byte* raw = (byte*)&header;
                string name = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes);

                byte[] ansiName = AnsiCodePage.ToAnsiZ(name);
                fixed (byte* entryName = ansiName)
                    results.Add((name, plugin.ProcessFile(handle, Wcx.PK_EXTRACT, targetPath, entryName)));
            }

            plugin.CloseArchive(handle);
        }

        return results;
    }

    /// <summary>ANSI-близнец VerifyAllW: PK_TEST без суффикса W. На диск не пишет — потому и проходит.</summary>
    private static int VerifyAllA(Plugin plugin, string container)
    {
        int errors = 0;

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(container);
        byte[] comment = new byte[4096];

        fixed (byte* archiveName = ansiArchive)
        fixed (byte* commentBuffer = comment)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive для PK_TEST вернул 0 для {Quote(container)}");
                return -1;
            }

            while (true)
            {
                tHeaderDataEx header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderEx(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ANSI ReadHeaderEx в {Quote(container)} вернул {Wcx.ErrorName(code)}");
                    errors++;
                    break;
                }

                if (plugin.ProcessFile(handle, Wcx.PK_TEST, null, null) != 0)
                    errors++;
            }

            plugin.CloseArchive(handle);
        }

        return errors;
    }

    // ─────────────────────────── сверка с тем, что должно лежать ───────────────────────────

    private static void CheckListing(List<ArcEntry> listing, ContainerModel model, string what)
    {
        Dictionary<string, bool> expected = model.Entries();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrong = new List<string>();

        foreach (ArcEntry entry in listing)
        {
            if (!seen.Add(entry.Name))
            {
                wrong.Add($"{Quote(entry.Name)} повторяется в списке");
                continue;
            }

            if (!expected.TryGetValue(entry.Name, out bool shouldBeDirectory))
                wrong.Add($"{Quote(entry.Name)} есть в списке, а быть его не должно");
            else if (shouldBeDirectory != entry.IsDirectory)
                wrong.Add($"{Quote(entry.Name)}: показан как {(entry.IsDirectory ? "папка" : "файл")}, "
                    + $"ожидалось {(shouldBeDirectory ? "папка" : "файл")}");
        }

        foreach (KeyValuePair<string, bool> pair in expected)
            if (!seen.Contains(pair.Key))
                wrong.Add($"{Quote(pair.Key)} ({(pair.Value ? "папка" : "файл")}) должно быть, но его нет");

        Check(wrong.Count == 0,
            $"{what}: в списке {listing.Count} записей, ожидалось {expected.Count}"
            + (wrong.Count == 0 ? "" : $" — {Join(wrong.Take(6))}"));

        foreach (string problem in wrong.Take(12))
            Console.WriteLine($"    ! {problem}");
    }

    private static void CompareModel(string actualRoot, ContainerModel model, string what)
    {
        Snapshot actual = Snapshot.Collect(actualRoot);
        var problems = new List<string>();

        foreach (KeyValuePair<string, (long Size, string Sha256)> expected in model.Files)
        {
            if (!actual.Files.TryGetValue(expected.Key, out (long Size, string Sha256) got))
            {
                problems.Add($"нет файла {Quote(expected.Key)}");
                continue;
            }

            if (got.Sha256 != expected.Value.Sha256)
                problems.Add($"содержимое {Quote(expected.Key)} отличается от оригинала");
            else if (got.Size != expected.Value.Size)
                problems.Add($"размер {Quote(expected.Key)}: {got.Size} вместо {expected.Value.Size}");
        }

        foreach (KeyValuePair<string, (long Size, string Sha256)> extra in actual.Files)
            if (!model.Files.ContainsKey(extra.Key))
                problems.Add($"лишний файл {Quote(extra.Key)}");

        foreach (KeyValuePair<string, bool> entry in model.Entries())
            if (entry.Value && !Directory.Exists(Path.Combine(actualRoot, entry.Key)))
                problems.Add($"нет папки {Quote(entry.Key)}");

        Check(problems.Count == 0,
            $"{what}: файлов {model.FileCount}, папок {model.DirectoryCount} — "
            + (problems.Count == 0 ? "всё совпадает побайтово" : Join(problems.Take(6))));

        foreach (string problem in problems.Take(12))
            Console.WriteLine($"    ! {problem}");
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool NameUnder(string folder, string path) =>
        path.Length > folder.Length && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private static string YesNo(bool value) => value ? "есть" : "нет";

    private static void Recreate(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        Directory.CreateDirectory(directory);
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text);

    /// <summary>Детерминированные байты: один и тот же файл при каждом запуске харнесса.</summary>
    private static void WritePattern(string path, int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)((i * 131 + (i >> 7) * 17 + (i >> 3)) & 0xFF);

        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>
/// Что должно лежать в контейнере после операций харнесса. Модель ведётся параллельно вызовам
/// плагина и потом сверяется и со списком архива, и с фактически извлечённым деревом.
///
/// Разница важна: панель показывает папку и как запись индекса (пустая папка), и как недостающий
/// промежуточный каталог («a\b\c.bin» без записи «a»). Каталог, из которого всё удалили, исчезает
/// из списка, поэтому здесь хранятся файлы и явные пустые папки, а остальное выводится из них.
/// </summary>
internal sealed class ContainerModel
{
    private readonly Dictionary<string, (long Size, string Sha256)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _emptyFolders = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, (long Size, string Sha256)> Files => _files;
    public int FileCount => _files.Count;
    public int DirectoryCount => Entries().Count(e => e.Value);

    public void AddFileFromDisk(string entryName, string diskPath)
    {
        long size = new FileInfo(diskPath).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(diskPath)));
        AddFile(entryName, size, hash);
    }

    public void MergeFrom(Snapshot source)
    {
        foreach (KeyValuePair<string, (long Size, string Sha256)> file in source.Files)
            AddFile(file.Key, file.Value.Size, file.Value.Sha256);

        foreach (string directory in EmptyLeafDirectories(source))
            _emptyFolders.Add(directory);
    }

    public void Remove(string entryName)
    {
        string name = entryName.TrimEnd('\\');
        List<string> removed = new();

        foreach (string key in _files.Keys.Where(f => NameUnder(name, f)).ToList())
        {
            _files.Remove(key);
            removed.Add(key);
        }

        foreach (string key in _emptyFolders.Where(d => NameUnder(name, d)).ToList())
        {
            _emptyFolders.Remove(key);
            removed.Add(key);
        }

        if (_files.Remove(name))
            removed.Add(name);

        if (_emptyFolders.Remove(name))
            removed.Add(name);

        // Ядро оставляет в контейнере папку, из которой удалён последний файл, — иначе панель теряет
        // её так же, как «проводник» потерял бы папку после удаления из неё последнего файла.
        // Модель обязана думать идентично, иначе сверка списка после удаления станет случайной.
        foreach (string entry in removed)
            foreach (string folder in Ancestors(entry).Reverse())
            {
                // Папку удалили по имени (целиком или вместе с предком): обратно её не возвращаем.
                if (folder == name || NameUnder(name, folder))
                    continue;

                // Под папкой что-то осталось: она видна из имён этих записей, вторая запись не нужна.
                if (_emptyFolders.Contains(folder)
                    || _emptyFolders.Any(other => NameUnder(folder, other))
                    || _files.Keys.Any(file => NameUnder(folder, file)))
                    break;

                _emptyFolders.Add(folder);
            }
    }

    /// <summary>Имя → папка ли это. Именно так список панели собирается из записей индекса.</summary>
    public Dictionary<string, bool> Entries()
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in _emptyFolders)
            entries[folder] = true;

        foreach (KeyValuePair<string, (long Size, string Sha256)> file in _files)
            entries[file.Key] = false;

        foreach (string name in entries.Keys.ToList())
            foreach (string ancestor in Ancestors(name))
                entries[ancestor] = true;

        return entries;
    }

    private void AddFile(string name, long size, string sha256)
    {
        _files[name] = (size, sha256);

        // Ядро убирает запись пустой папки, когда под ней появились дети: папка остаётся видна,
        // но уже как промежуточный каталог, выведенный из детей.
        foreach (string ancestor in Ancestors(name))
            _emptyFolders.Remove(ancestor);
    }

    /// <summary>Папки без файлов и без вложенных папок: при упаковке они становятся записями индекса.</summary>
    private static IEnumerable<string> EmptyLeafDirectories(Snapshot source)
    {
        foreach (string directory in source.Directories)
        {
            bool hasChildDirectory = source.Directories.Any(other => NameUnder(directory, other));
            bool hasFile = source.Files.Keys.Any(file => NameUnder(directory, file));

            if (!hasChildDirectory && !hasFile)
                yield return directory;
        }
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        int index = path.IndexOf('\\');
        while (index > 0)
        {
            yield return path[..index];
            index = path.IndexOf('\\', index + 1);
        }
    }

    private static bool NameUnder(string folder, string path) =>
        path.Length > folder.Length && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
}
