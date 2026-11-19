using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WcxHarness;

/// <summary>
/// Проверка WCX-плагина без Total Commander. Харнесс повторяет последовательности вызовов TC
/// и сверяет список и распакованные файлы с оригиналом (размер + SHA-256).
///
///   --plugin    <путь к .wcx64>          обязательный
///   --container <путь к .cc>             обязательный
///   --source    <папка-оригинал>         обязательный (с ним сверяются список и хэши)
///   --workdir   <папка для распаковки>   обязательный, пересоздаётся
///   --compare-plugin <путь к .wcx64>     необязательный: другой реальныx WCX-плагин
///   --compare-archive <путь к архиву>    для сравнения: как он описывает папки
///   --results <путь к .tsv>              machine-readable итог: секция, проверка, OK/FAIL
///                                        (нужен xUnit-обёртке ContainerWcx.Tests)
///   --layout-only                        только проверки раскладки структур: плагин и контейнер
///                                        не нужны, секции проходов не открываются
///   --skip-settings                      пропустить проход 14.1 (диалог «Настроить» требует
///                                        интерактивного рабочего стола, в CI его нет)
/// </summary>
internal static unsafe partial class Program
{
    private static readonly List<string> Failures = new();
    private static int _checks;

    /// <summary>Отметка о каждой проверке с её секцией — для --results. Порядок = порядок вызовов.</summary>
    private readonly record struct CheckRecord(string Section, string What, bool Ok);

    private static readonly List<CheckRecord> Records = new();
    private static string _currentSection = "без секции";

    /// <summary>Куда --results пишет machine-readable итог (null — не пишем).</summary>
    private static string? ResultsPath;

    private static int Main(string[] args)
    {
        // Сообщения харнесса — кириллицей. Консоль Windows кодирует вывод кодовой страницей OEM:
        // на русскоязычной машине это 866 и кириллица проходит, на CI-раннере (США) — 437, где
        // каждый знак превращается в '?'. Кодировка фиксируется явно и без BOM, чтобы читающая
        // сторона (ContainerWcx.Tests) могла сверять строки на любой машине.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Options? options = Options.Parse(args);
        if (options is null)
            return 2;

        ResultsPath = options.ResultsPath;

        if (options.LayoutOnly)
        {
            // Раскладку структур сверяем с официальным wcxhead.h — плагин для этого не нужен,
            // поэтому тесты раскладки гоняют харнесс без --plugin/--container/--source/--workdir.
            CheckLayout();
            CheckAnsiLayout();
            return Report();
        }

        Console.WriteLine($"Плагин:    {options.Plugin}");
        Console.WriteLine($"Контейнер: {options.Container}");
        Console.WriteLine($"Оригинал:  {options.Source}");
        Console.WriteLine();

        CheckLayout();
        CheckAnsiLayout();

        try
        {
            Plugin plugin = Plugin.Load(options.Plugin);

            int caps = plugin.GetPackerCaps();
            Console.WriteLine($"GetPackerCaps = {Wcx.CapsName(caps)}");
            if (plugin.HasBackgroundFlags)
                Console.WriteLine($"GetBackgroundFlags = {plugin.GetBackgroundFlags()}");

            // TC вызовет PackFiles/DeleteFiles только если плагин их заявил. Заявил возможности, а
            // функции нет — TC дойдёт до вызова и получит падение, поэтому это проверка, а не заметка.
            Check(!(HasPackCaps(caps)) || plugin.HasPackW || plugin.HasPackAnsi,
                "плагин заявил PK_CAPS_NEW|MODIFY и при этом экспортирует PackFilesW или PackFiles");
            Check((caps & Wcx.PK_CAPS_DELETE) == 0 || plugin.HasDeleteW || plugin.HasDeleteAnsi,
                "плагин заявил PK_CAPS_DELETE и при этом экспортирует DeleteFilesW или DeleteFiles");
            Check(!HasPackCaps(caps) || plugin.HasPackDefaults,
                "плагин заявил упаковку и при этом принимает PackSetDefaultParams");

            // TC показывает кнопку «Настроить» только при PK_CAPS_OPTIONS и по нажатию зовёт ConfigurePacker.
            // Заявлен флаг без функции — TC зовёт несуществующий экспорт; функция без флага — настроек не видно.
            Check((caps & Wcx.PK_CAPS_OPTIONS) == 0 || plugin.HasConfigure,
                "плагин заявил PK_CAPS_OPTIONS и при этом экспортирует ConfigurePacker");
            Check(!plugin.HasConfigure || (caps & Wcx.PK_CAPS_OPTIONS) != 0,
                "экспорт ConfigurePacker есть и при этом заявлен PK_CAPS_OPTIONS — иначе TC не покажет кнопку «Настроить»");
            Console.WriteLine(plugin.HasConfigure
                ? "ConfigurePacker: экспорт найден, диалог настроек проверим отдельным проходом"
                : "ConfigurePacker: экспорта нет — диалога настроек у плагина нет");
            Console.WriteLine();

            List<ArcEntry> listing = ReadListing(plugin, options.Container);
            Check(listing.Count > 0, $"список непустой ({listing.Count} записей)");

            Snapshot original = Snapshot.Collect(options.Source);
            CompareListing(listing, original);

            RunExtract(plugin, options, listing);
            RunVerify(plugin, options.Container, listing);
            RunAbort(plugin, options);
            RunBrokenInputs(plugin, options);
            RunHandleHygiene(plugin, options.Container);

            if (plugin.HasAnsiBranch)
            {
                RunAnsiBranch(plugin, options, listing);
            }
            else
            {
                Section("ANSI-ветка «первого издания»");
                Console.WriteLine($"  ПРОПУСК: из DLL не получены экспорты {Join(plugin.MissingAnsiExports)} — "
                    + "эти экспорты не обязательны, поэтому ANSI-проверки не выполнялись (юникод-проверки выше в силе)");
                Console.WriteLine();
            }

            if (options.ComparePlugin is not null && options.CompareArchive is not null)
                ReadForeignListing(options);

            RunPack(plugin, options);
            RunPackAnsi(plugin, options);
        }
        catch (Exception ex)
        {
            Fail($"харнесс прерван исключением: {ex.GetType().Name}: {ex.Message}");
        }

        return Report();
    }

    // ─────────────────────────── раскладка структур ───────────────────────────

    private static void CheckLayout()
    {
        Section("раскладка структур wcxhead.h для x64");

        Check(sizeof(tOpenArchiveDataW) == Wcx.SizeOfOpenData,
            $"sizeof(tOpenArchiveDataW) == {Wcx.SizeOfOpenData}, получено {sizeof(tOpenArchiveDataW)}");
        Check(sizeof(tHeaderDataExW) == Wcx.SizeOfHeader,
            $"sizeof(tHeaderDataExW) == {Wcx.SizeOfHeader}, получено {sizeof(tHeaderDataExW)}");

        tOpenArchiveDataW open = default;
        byte* openBase = (byte*)&open;

        Check(Off(openBase, &open.ArcName) == Wcx.OffOpenArcName, "tOpenArchiveDataW.ArcName на 0");
        Check(Off(openBase, &open.OpenMode) == Wcx.OffOpenMode, "tOpenArchiveDataW.OpenMode на 8");
        Check(Off(openBase, &open.OpenResult) == Wcx.OffOpenResult, "tOpenArchiveDataW.OpenResult на 12");
        Check(Off(openBase, &open.CmtBuf) == Wcx.OffOpenCmtBuf, "tOpenArchiveDataW.CmtBuf на 16 (выровнен по 8 после int)");
        Check(Off(openBase, &open.CmtBufSize) == Wcx.OffOpenCmtBufSize, "tOpenArchiveDataW.CmtBufSize на 24");
        Check(Off(openBase, &open.CmtSize) == Wcx.OffOpenCmtSize, "tOpenArchiveDataW.CmtSize на 28");
        Check(Off(openBase, &open.CmtState) == Wcx.OffOpenCmtState, "tOpenArchiveDataW.CmtState на 32");

        tHeaderDataExW header = default;
        byte* head = (byte*)&header;
        // Имя поля fixed-buffer в небезопасном коде уже является указателем на начало буфера.
        int arcNameOffset = Off(head, header.ArcName);
        int fileNameOffset = Off(head, header.FileName);
        int reservedOffset = Off(head, header.Reserved);

        Check(arcNameOffset == Wcx.OffArcName, "tHeaderDataExW.ArcName на 0");
        Check(fileNameOffset == Wcx.OffFileName, "tHeaderDataExW.FileName на 2048");
        Check(Off(head, &header.Flags) == Wcx.OffFlags, "tHeaderDataExW.Flags на 4096");
        Check(Off(head, &header.PackSize) == Wcx.OffPackSize, "PackSize на 4100");
        Check(Off(head, &header.PackSizeHigh) == Wcx.OffPackSizeHigh, "PackSizeHigh на 4104");
        Check(Off(head, &header.UnpSize) == Wcx.OffUnpSize, "UnpSize на 4108");
        Check(Off(head, &header.UnpSizeHigh) == Wcx.OffUnpSizeHigh, "UnpSizeHigh на 4112");
        Check(Off(head, &header.HostOS) == Wcx.OffHostOS, "HostOS на 4116");
        Check(Off(head, &header.FileCRC) == Wcx.OffFileCRC, "FileCRC на 4120");
        Check(Off(head, &header.FileTime) == Wcx.OffFileTime, "FileTime на 4124");
        Check(Off(head, &header.UnpVer) == Wcx.OffUnpVer, "UnpVer на 4128");
        Check(Off(head, &header.Method) == Wcx.OffMethod, "Method на 4132");
        Check(Off(head, &header.FileAttr) == Wcx.OffFileAttr, "FileAttr на 4136");
        Check(Off(head, &header.CmtBuf) == Wcx.OffCmtBuf, "CmtBuf на 4144 (выровнен по 8 после int FileAttr)");
        Check(Off(head, &header.CmtBufSize) == Wcx.OffCmtBufSize, "CmtBufSize на 4152");
        Check(Off(head, &header.CmtSize) == Wcx.OffCmtSize, "CmtSize на 4156");
        Check(Off(head, &header.CmtState) == Wcx.OffCmtState, "CmtState на 4160");
        Check(reservedOffset == Wcx.OffReserved, "Reserved на 4164");
        Check(reservedOffset + 1024 <= Wcx.SizeOfHeader && Wcx.SizeOfHeader - reservedOffset - 1024 < 8,
            $"Reserved занимает 1024 байта (до {reservedOffset + 1024}), а {Wcx.SizeOfHeader - reservedOffset - 1024} байта — хвост выравнивания по 8");

        Console.WriteLine();
    }

    private static int Off(byte* base_, void* field) => (int)((byte*)field - base_);

    // ─────────────────────────── проход списка ───────────────────────────

    /// <summary>Открыл, прочитал весь список (ReadHeaderExW + PK_SKIP) и закрыл — ровно как TC в панели.</summary>
    private static List<ArcEntry> ReadListing(Plugin plugin, string container, bool verbose = true)
    {
        if (verbose)
            Section("проход 1: список (PK_OM_LIST) — то, что покажет панель TC");

        List<ArcEntry> entries = new();
        char[] comment = new char[4096];

        fixed (char* archiveName = container)
        fixed (char* commentBuffer = comment)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW(PK_OM_LIST) вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return entries;
            }

            Check(handle != 0 && data.OpenResult == 0, $"OpenArchiveW(PK_OM_LIST): handle выдан, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
            // SDK (tHeaderDataEx): «Cmt* … currently not used in Total Commander, so they may be set to zero».
            Check(data.CmtState is 0 or 1, $"CmtState в допустимых значениях 0/1 (комментария нет), получено {data.CmtState}");

            plugin.SetChangeVolProcW(handle, 0);
            using Progress progress = new();
            plugin.SetProcessDataProcW(handle, progress.Handle);

            bool offsetsMatched = true;
            bool reservedCleared = true;

            while (true)
            {
                tHeaderDataExW header = default;
                header.CmtBuf = commentBuffer;      // TC заполняет буфер комментария до вызова
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderExW(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ReadHeaderExW вернул {Wcx.ErrorName(code)} после {entries.Count} записей");
                    break;
                }

                byte* raw = (byte*)&header;
                offsetsMatched &= RawFieldsMatchTypedFields(raw, &header);
                reservedCleared &= IsZero(raw + Wcx.OffReserved, 1024);

                entries.Add(new ArcEntry(
                    ReadUtf16(raw + Wcx.OffFileName, 1024),
                    Combine(ReadUInt32(raw + Wcx.OffUnpSizeHigh), ReadUInt32(raw + Wcx.OffUnpSize)),
                    Combine(ReadUInt32(raw + Wcx.OffPackSizeHigh), ReadUInt32(raw + Wcx.OffPackSize)),
                    ReadInt32(raw + Wcx.OffFileAttr),
                    ReadInt32(raw + Wcx.OffFileTime),
                    (ReadInt32(raw + Wcx.OffFileAttr) & Wcx.FA_DIRECTORY) != 0));

                int skipped = plugin.ProcessFileW(handle, Wcx.PK_SKIP, null, null);
                if (skipped != 0)
                {
                    Fail($"ProcessFile(PK_SKIP) вернул {Wcx.ErrorName(skipped)}");
                    break;
                }
            }

            int closed = plugin.CloseArchive(handle);
            Check(closed == 0, $"CloseArchive == 0, получено {Wcx.ErrorName(closed)}");
            Check(offsetsMatched, $"значения в tHeaderDataExW видны на смещениях x64 во всех {entries.Count} записях");
            Check(reservedCleared, $"Reserved обнулён во всех {entries.Count} записях (требование SDK 2.21)");
        }

        if (verbose)
        {
            foreach (ArcEntry entry in entries)
                Console.WriteLine($"  {(entry.IsDirectory ? "папка" : "файл ")}  {Quote(entry.Name),-46} {entry.UnpSize,12}  сжатый {entry.PackSize}");
            Console.WriteLine();
        }

        return entries;
    }

    private static bool RawFieldsMatchTypedFields(byte* raw, tHeaderDataExW* header)
    {
        string rawName = ReadUtf16(raw + Wcx.OffFileName, 1024);
        string rawArchive = ReadUtf16(raw + Wcx.OffArcName, 1024);

        // Для поля-буфера «fixed char[1024]» в небезопасном коде доступно прямое имя как char*.
        string typedName = new string(header->FileName);
        string typedArchive = new string(header->ArcName);

        return rawName == typedName
            && rawArchive == typedArchive
            && ReadInt32(raw + Wcx.OffFileAttr) == header->FileAttr
            && ReadInt32(raw + Wcx.OffFlags) == header->Flags
            && ReadInt32(raw + Wcx.OffFileTime) == header->FileTime
            && ReadInt32(raw + Wcx.OffHostOS) == header->HostOS
            && ReadInt32(raw + Wcx.OffFileCRC) == header->FileCRC
            && ReadUInt32(raw + Wcx.OffUnpSize) == header->UnpSize
            && ReadUInt32(raw + Wcx.OffUnpSizeHigh) == header->UnpSizeHigh
            && ReadUInt32(raw + Wcx.OffPackSize) == header->PackSize
            && ReadUInt32(raw + Wcx.OffPackSizeHigh) == header->PackSizeHigh
            && ReadInt32(raw + Wcx.OffCmtBufSize) == header->CmtBufSize
            && ReadInt32(raw + Wcx.OffCmtSize) == header->CmtSize
            && ReadInt32(raw + Wcx.OffCmtState) == header->CmtState;
    }

    private static void CompareListing(List<ArcEntry> listing, Snapshot original,
        string title = "сверка списка контейнера с оригиналом на диске")
    {
        Section(title);

        HashSet<string> listedFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> listedDirs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> listedSizes = new(StringComparer.OrdinalIgnoreCase);

        foreach (ArcEntry entry in listing)
        {
            string name = entry.Name.TrimEnd('\\');
            if (entry.IsDirectory)
                listedDirs.Add(name);
            else
            {
                listedFiles.Add(name);
                listedSizes[name] = entry.UnpSize;
            }
        }

        List<string> missingFiles = original.Files.Keys.Where(f => !listedFiles.Contains(f)).ToList();
        List<string> extraFiles = listedFiles.Where(f => !original.Files.ContainsKey(f)).ToList();
        List<string> missingDirs = original.Directories.Where(d => !listedDirs.Contains(d)).ToList();
        List<string> wrongSizes = new();
        foreach (string file in listedFiles)
        {
            if (original.Files.TryGetValue(file, out ValueTuple<long, string> entry) && listedSizes[file] != entry.Item1)
                wrongSizes.Add($"{file}: в контейнере {listedSizes[file]}, на диске {entry.Item1}");
        }

        Check(missingFiles.Count == 0, $"все файлы оригинала видны в списке (не хватает {missingFiles.Count}: {Join(missingFiles)})");
        Check(extraFiles.Count == 0, $"в списке нет файлов, которых нет в оригинале (лишние: {Join(extraFiles)})");
        Check(missingDirs.Count == 0, $"все папки оригинала видны в списке, включая пустые (не хватает {missingDirs.Count}: {Join(missingDirs)})");
        Check(wrongSizes.Count == 0, $"размеры в списке совпадают с оригиналом ({Join(wrongSizes)})");

        int duplicated = listing.Count - listing.Select(e => e.Name.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Check(duplicated == 0, $"в списке нет дублей имён (найдено {duplicated})");

        Console.WriteLine($"  записей в списке: {listing.Count} (папок {listing.Count(e => e.IsDirectory)}, файлов {listing.Count(e => !e.IsDirectory)})");
        Console.WriteLine();
    }

    // ─────────────────────────── извлечение ───────────────────────────

    private static void RunExtract(Plugin plugin, Options options, List<ArcEntry> listPass)
    {
        Section("проход 2: извлечение (PK_OM_EXTRACT) — копирование файлов ИЗ контейнера");

        string target = Path.Combine(options.WorkDir, "extract");
        if (Directory.Exists(target))
            Directory.Delete(target, true);
        Directory.CreateDirectory(target);

        string renamedDir = Path.Combine(options.WorkDir, "renamed");
        if (Directory.Exists(renamedDir))
            Directory.Delete(renamedDir, true);
        Directory.CreateDirectory(renamedDir);

        char[] comment = new char[4096];
        List<ArcEntry> extractPass = new();
        int extractErrors = 0;
        string firstExtractError = string.Empty;
        int progressCalls = 0;
        long progressBytes = 0;
        bool renamedOk = false;
        bool fullPathVariantOk = false;
        const string RenamedLeaf = "Zzz переименован.bin";
        string renamedSource = string.Empty;
        string renamedFull = Path.Combine(renamedDir, RenamedLeaf);

        fixed (char* archiveName = options.Container)
        fixed (char* commentBuffer = comment)
        fixed (char* targetPath = target)
        fixed (char* renamedPath = renamedDir)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW(PK_OM_EXTRACT) вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            using Progress progress = new();
            plugin.SetProcessDataProcW(handle, progress.Handle);

            while (true)
            {
                tHeaderDataExW header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderExW(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ReadHeaderExW(PK_OM_EXTRACT) вернул {Wcx.ErrorName(code)}");
                    break;
                }

                byte* raw = (byte*)&header;
                ArcEntry entry = new(
                    ReadUtf16(raw + Wcx.OffFileName, 1024),
                    Combine(ReadUInt32(raw + Wcx.OffUnpSizeHigh), ReadUInt32(raw + Wcx.OffUnpSize)),
                    Combine(ReadUInt32(raw + Wcx.OffPackSizeHigh), ReadUInt32(raw + Wcx.OffPackSize)),
                    ReadInt32(raw + Wcx.OffFileAttr),
                    0,
                    (ReadInt32(raw + Wcx.OffFileAttr) & Wcx.FA_DIRECTORY) != 0);

                extractPass.Add(entry);

                fixed (char* entryName = entry.Name)
                {
                    int extracted = plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, targetPath, entryName);
                    if (extracted != 0)
                    {
                        extractErrors++;
                        if (firstExtractError.Length == 0)
                            firstExtractError = $"{Quote(entry.Name)} → {Wcx.ErrorName(extracted)}";
                    }
                }

                // Разные варианты обращения с именем, как это делает TC:
                // 1) переименование при копировании — DestPath + новое имя;
                if (!entry.IsDirectory && renamedSource.Length == 0 && entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    renamedSource = entry.Name;
                    const string NewName = "Zzz переименован.bin";
                    fixed (char* newName = NewName)
                        renamedOk = plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, renamedPath, newName) == 0;
                }
                // 2) полный целевой путь в DestName, DestPath = NULL.
                else if (!entry.IsDirectory && !fullPathVariantOk && entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    fullPathVariantOk = true;
                    string full = Path.Combine(renamedDir, "whole-path.txt");
                    fixed (char* fullPath = full)
                        fullPathVariantOk = plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, null, fullPath) == 0;
                }
            }

            plugin.CloseArchive(handle);

            progressCalls = progress.Calls;
            progressBytes = progress.Bytes;

            Check(extractErrors == 0, $"все записи извлечены без ошибок (ошибок {extractErrors}: {firstExtractError})");
            Check(progressCalls > 0, $"плагин сообщил прогресс через tProcessDataProcW ({progressCalls} вызовов, {progressBytes} байт)");
            Check(renamedOk, "копирование с переименованием (DestPath + новое имя) удалось");
            Check(fullPathVariantOk, "копирование с полным путём в DestName (DestPath = NULL) удалось");
        }

        Check(extractPass.Count == listPass.Count &&
              extractPass.Select(e => e.Name).SequenceEqual(listPass.Select(e => e.Name), StringComparer.OrdinalIgnoreCase),
              $"проходы PK_OM_LIST и PK_OM_EXTRACT дали одинаковый список ({extractPass.Count} против {listPass.Count})");

        Snapshot afterExtract = Snapshot.Collect(target);
        Snapshot original = Snapshot.Collect(options.Source);

        List<string> lost = new();
        List<string> extra = new();
        List<string> badHash = new();
        List<string> zeroLength = new();

        foreach (KeyValuePair<string, (long Size, string Sha256)> source in original.Files)
        {
            string relative = source.Key;

            if (!afterExtract.Files.TryGetValue(relative, out ValueTuple<long, string> extractedFile))
            {
                lost.Add(relative);
                continue;
            }

            if (extractedFile.Item2 != source.Value.Sha256)
                badHash.Add(relative);

            if (source.Value.Size > 0 && extractedFile.Item1 == 0)
                zeroLength.Add(relative);
        }

        foreach (string relative in afterExtract.Files.Keys)
            if (!original.Files.ContainsKey(relative))
                extra.Add(relative);

        List<string> noDirs = original.Directories.Where(d => !Directory.Exists(Path.Combine(target, d))).ToList();

        Check(lost.Count == 0, $"каждый файл оригинала появился после извлечения (не хватает {lost.Count}: {Join(lost)})");
        Check(extra.Count == 0, $"после извлечения нет лишних файлов (лишние: {Join(extra)})");
        Check(badHash.Count == 0, $"SHA-256 всех извлечённых файлов совпадает с оригиналом (расходятся: {Join(badHash)})");
        Check(zeroLength.Count == 0, $"не пустые файлы не оказались нулевыми ({Join(zeroLength)})");
        Check(noDirs.Count == 0, $"все папки, включая пустые, созданы (не созданы: {Join(noDirs)})");

        if (renamedSource.Length > 0 && File.Exists(renamedFull))
        {
            bool identical = original.Files.TryGetValue(renamedSource, out ValueTuple<long, string> sourceEntry)
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(renamedFull))) == sourceEntry.Item2;

            Check(identical, $"переименованный файл '{Path.GetFileName(renamedFull)}' побайтово равен оригиналу '{renamedSource}'");
        }
        else
            Fail($"переименованный файл не появился на диске: {renamedFull}");

        string wholePathFile = Path.Combine(renamedDir, "whole-path.txt");
        if (File.Exists(wholePathFile))
            Console.WriteLine($"  полный путь в DestName записан: {wholePathFile} ({new FileInfo(wholePathFile).Length} байт)");

        Console.WriteLine();
    }

    // ─────────────────────────── проверка целостности ───────────────────────────

    private static void RunVerify(Plugin plugin, string container, List<ArcEntry> listing)
    {
        Section("проход 3: проверка (PK_TEST) — целостность без записи на диск");

        char[] comment = new char[4096];
        int checkedFiles = 0;
        int errors = 0;

        fixed (char* archiveName = container)
        fixed (char* commentBuffer = comment)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW для PK_TEST вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            while (true)
            {
                tHeaderDataExW header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderExW(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                    break;

                if (code != 0)
                {
                    Fail($"ReadHeaderExW вернул {Wcx.ErrorName(code)} в проходе PK_TEST");
                    break;
                }

                byte* raw = (byte*)&header;
                bool isDirectory = (ReadInt32(raw + Wcx.OffFileAttr) & Wcx.FA_DIRECTORY) != 0;

                int result = plugin.ProcessFileW(handle, Wcx.PK_TEST, null, null);
                if (result != 0)
                {
                    errors++;
                    Fail($"PK_TEST для {Quote(ReadUtf16(raw + Wcx.OffFileName, 1024))} вернул {Wcx.ErrorName(result)}");
                }

                if (!isDirectory)
                    checkedFiles++;
            }

            plugin.CloseArchive(handle);
        }

        Check(errors == 0 && checkedFiles > 0, $"PK_TEST пройден для всех {checkedFiles} файлов без ошибок");
        Console.WriteLine();
    }

    // ─────────────────────────── остановка пользователем ───────────────────────────

    private static void RunAbort(Plugin plugin, Options options)
    {
        Section("проход 4: пользователь нажал Stop (tProcessDataProcW возвращает 0)");

        char[] comment = new char[4096];
        string target = Path.Combine(options.WorkDir, "abort");
        if (Directory.Exists(target))
            Directory.Delete(target, true);
        Directory.CreateDirectory(target);

        fixed (char* archiveName = options.Container)
        fixed (char* commentBuffer = comment)
        fixed (char* targetPath = target)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail($"OpenArchiveW для теста остановки вернул 0");
                return;
            }

            using Progress progress = new(abort: true);
            plugin.SetProcessDataProcW(handle, progress.Handle);

            int code = 0;
            string name = string.Empty;
            bool isDirectory = true;

            while (isDirectory)
            {
                tHeaderDataExW header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                code = plugin.ReadHeaderExW(handle, &header);
                if (code != 0)
                    break;

                byte* raw = (byte*)&header;
                name = ReadUtf16(raw + Wcx.OffFileName, 1024);
                isDirectory = (ReadInt32(raw + Wcx.OffFileAttr) & Wcx.FA_DIRECTORY) != 0;

                if (isDirectory)
                    plugin.ProcessFileW(handle, Wcx.PK_SKIP, null, null);
            }

            int result = 0;
            if (!string.IsNullOrEmpty(name))
            {
                fixed (char* entryName = name)
                    result = plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, targetPath, entryName);
            }

            plugin.CloseArchive(handle);

            Check(code == Wcx.E_END_ARCHIVE || !string.IsNullOrEmpty(name), "найден первый файловый вход для остановки");
            Check(result == Wcx.E_EABORTED, $"остановка вернула E_EABORTED, получено {Wcx.ErrorName(result)}");
            Check(progress.Calls > 0, $"плагин спросил разрешение через tProcessDataProcW ({progress.Calls} раз)");
            Check(!File.Exists(Path.Combine(target, name.TrimEnd('\\'))), $"прерванный файл не был создан ({Quote(name)})");
        }

        Console.WriteLine();
    }

    // ─────────────────────────── битые входные данные ───────────────────────────

    private static void RunBrokenInputs(Plugin plugin, Options options)
    {
        Section("проход 5: не контейнер и отсутствующий файл");

        string notAContainer = Path.Combine(options.WorkDir, "не-контейнер.txt");
        File.WriteAllText(notAContainer, "это не контейнер Container_V3, плагин обязан честно отказать.");

        Check(RefuseOpen(plugin, notAContainer, "обычный текстовый файл"), "отказ при открытии текстового файла корректный");

        string missing = Path.Combine(options.WorkDir, "нет-такого-файла.cc");
        Check(RefuseOpen(plugin, missing, "несуществующий .cc"), "отказ при открытии отсутствующего файла корректный");

        Console.WriteLine();
    }

    private static bool RefuseOpen(Plugin plugin, string path, string what)
    {
        fixed (char* name = path)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = name;
            data.OpenMode = Wcx.PK_OM_LIST;

            nint handle = plugin.OpenArchiveW(&data);
            bool refused = handle == 0 && data.OpenResult != 0;

            Console.WriteLine($"  {what}: handle = {(handle == 0 ? "0" : "выдан")}, OpenResult = {Wcx.ErrorName(data.OpenResult)}");

            if (handle != 0)
                plugin.CloseArchive(handle);

            return refused;
        }
    }

    // ─────────────────────────── гигиена handle ───────────────────────────

    private static void RunHandleHygiene(Plugin plugin, string container)
    {
        Section("проход 6: чужой handle, повторный CloseArchive, ProcessFile без ReadHeaderEx");

        fixed (char* archiveName = container)
        {
            tOpenArchiveDataW data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;

            nint handle = plugin.OpenArchiveW(&data);
            if (handle == 0)
            {
                Fail("OpenArchiveW для теста гигиены вернул 0");
                return;
            }

            int withoutHeader = plugin.ProcessFileW(handle, Wcx.PK_EXTRACT, null, null);
            Check(withoutHeader == Wcx.E_BAD_ARCHIVE, $"ProcessFile без предварительного ReadHeaderExW вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(withoutHeader)}");

            Check(plugin.CloseArchive(handle) == 0, "первый CloseArchive == 0");
            Check(plugin.CloseArchive(handle) == 0, "повторный CloseArchive того же handle не упал и вернул 0");

            tHeaderDataExW header = default;
            int afterClose = plugin.ReadHeaderExW(handle, &header);
            Check(afterClose == Wcx.E_BAD_ARCHIVE, $"ReadHeaderExW закрытого handle вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(afterClose)}");

            int zeroHandle = plugin.ReadHeaderExW(0, &header);
            Check(zeroHandle == Wcx.E_BAD_ARCHIVE, $"ReadHeaderExW с handle = 0 вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(zeroHandle)}");

            Check(plugin.CloseArchive(0) == 0, "CloseArchive(0) не упал и вернул 0");
        }

        Console.WriteLine();
    }

    // ─────────────────────────── как это делает реальный пакер ───────────────────────────

    private static void ReadForeignListing(Options options)
    {
        Section($"как описывает папки другой x64-пакер: {Path.GetFileName(options.ComparePlugin)} на {Path.GetFileName(options.CompareArchive)}");

        try
        {
            List<ArcEntry> entries = ReadListing(Plugin.Load(options.ComparePlugin!), options.CompareArchive!, verbose: false);
            foreach (ArcEntry entry in entries)
                Console.WriteLine($"  {(entry.IsDirectory ? "папка" : "файл ")}  {Quote(entry.Name),-46} {entry.UnpSize,12}  attrs 0x{entry.FileAttr:X2}");

            bool anyTrailingSlash = entries.Any(e => e.IsDirectory && e.Name.EndsWith('\\'));
            Console.WriteLine($"  папки с завершающим «\\»: {(anyTrailingSlash ? "да" : "нет")} (записей {entries.Count})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  сравнение не получилось: {ex.Message}");
        }

        Console.WriteLine();
    }

    // ─────────── раскладка ANSI-структур «первого издания» ───────────

    private static void CheckAnsiLayout()
    {
        Section("раскладка ANSI-структур «первого издания» (wcxhead.h) для x64");

        Check(sizeof(tOpenArchiveData) == WcxAnsi.SizeOfOpenData,
            $"sizeof(tOpenArchiveData) == {WcxAnsi.SizeOfOpenData}, получено {sizeof(tOpenArchiveData)}");
        Check(sizeof(tHeaderData) == WcxAnsi.SizeOfHeader,
            $"sizeof(tHeaderData) == {WcxAnsi.SizeOfHeader}, получено {sizeof(tHeaderData)}");
        Check(sizeof(tHeaderDataEx) == WcxAnsi.SizeOfHeaderEx,
            $"sizeof(tHeaderDataEx) == {WcxAnsi.SizeOfHeaderEx}, получено {sizeof(tHeaderDataEx)}");

        tOpenArchiveData open = default;
        byte* openBase = (byte*)&open;

        // По wcxhead.h ANSI-имя архива — указатель char* ArcName, как WCHAR* в W-версии.
        Check(Off(openBase, &open.ArcName) == WcxAnsi.OffOpenArcName, "tOpenArchiveData.ArcName (char*) на 0");
        Check(Off(openBase, &open.OpenMode) == WcxAnsi.OffOpenMode, "tOpenArchiveData.OpenMode на 8");
        Check(Off(openBase, &open.OpenResult) == WcxAnsi.OffOpenResult, "tOpenArchiveData.OpenResult на 12");
        Check(Off(openBase, &open.CmtBuf) == WcxAnsi.OffOpenCmtBuf, "tOpenArchiveData.CmtBuf на 16 (указатель выровнен по 8 после int)");
        Check(Off(openBase, &open.CmtBufSize) == WcxAnsi.OffOpenCmtBufSize, "tOpenArchiveData.CmtBufSize на 24");
        Check(Off(openBase, &open.CmtSize) == WcxAnsi.OffOpenCmtSize, "tOpenArchiveData.CmtSize на 28");
        Check(Off(openBase, &open.CmtState) == WcxAnsi.OffOpenCmtState, "tOpenArchiveData.CmtState на 32");
        Check(WcxAnsi.SizeOfOpenData == Wcx.SizeOfOpenData && Off(openBase, &open.CmtBuf) == Wcx.OffOpenCmtBuf,
            $"ANSI tOpenArchiveData совпадает с W-версией: {WcxAnsi.SizeOfOpenData} байт и те же смещения, потому что имя в обеих — указатель");

        tHeaderData header = default;
        byte* head = (byte*)&header;

        Check(Off(head, header.ArcName) == WcxAnsi.OffArcName, $"tHeaderData.ArcName — inline char[{WcxAnsi.NameBytes}] на {WcxAnsi.OffArcName}");
        Check(Off(head, header.FileName) == WcxAnsi.OffFileName, $"tHeaderData.FileName на {WcxAnsi.OffFileName} (следом после ArcName[260])");
        Check(Off(head, &header.Flags) == WcxAnsi.OffFlags, $"tHeaderData.Flags на {WcxAnsi.OffFlags}");
        Check(Off(head, &header.PackSize) == WcxAnsi.OffPackSize, $"tHeaderData.PackSize (signed int) на {WcxAnsi.OffPackSize}");
        Check(Off(head, &header.UnpSize) == WcxAnsi.OffUnpSize, $"tHeaderData.UnpSize (signed int) на {WcxAnsi.OffUnpSize}");
        Check(Off(head, &header.HostOS) == WcxAnsi.OffUnpSize + 4 && Off(head, &header.HostOS) == WcxAnsi.OffHostOS,
            $"сразу за UnpSize идёт HostOS ({WcxAnsi.OffHostOS}): в tHeaderData нет полей PackSizeHigh/UnpSizeHigh");
        Check(Off(head, &header.FileCRC) == WcxAnsi.OffFileCRC, $"FileCRC на {WcxAnsi.OffFileCRC}");
        Check(Off(head, &header.FileTime) == WcxAnsi.OffFileTime, $"FileTime на {WcxAnsi.OffFileTime}");
        Check(Off(head, &header.UnpVer) == WcxAnsi.OffUnpVer, $"UnpVer на {WcxAnsi.OffUnpVer}");
        Check(Off(head, &header.Method) == WcxAnsi.OffMethod, $"Method на {WcxAnsi.OffMethod}");
        Check(Off(head, &header.FileAttr) == WcxAnsi.OffFileAttr, $"FileAttr на {WcxAnsi.OffFileAttr}");
        Check(Off(head, &header.CmtBuf) == WcxAnsi.OffCmtBuf, $"CmtBuf на {WcxAnsi.OffCmtBuf} (выравнивание по 8 после int FileAttr, хвост 556..559)");
        Check(Off(head, &header.CmtBufSize) == WcxAnsi.OffCmtBufSize, $"CmtBufSize на {WcxAnsi.OffCmtBufSize}");
        Check(Off(head, &header.CmtSize) == WcxAnsi.OffCmtSize, $"CmtSize на {WcxAnsi.OffCmtSize}");
        Check(Off(head, &header.CmtState) == WcxAnsi.OffCmtState, $"CmtState на {WcxAnsi.OffCmtState}");
        Check(Off(head, &header.CmtState) + 4 == WcxAnsi.SizeOfHeader - 4
            && WcxAnsi.SizeOfHeader % 8 == 0, $"после CmtState структура дополняется 4 байтами выравнивания до {WcxAnsi.SizeOfHeader}");

        tHeaderDataEx ex = default;
        byte* exBase = (byte*)&ex;

        Check(Off(exBase, ex.ArcName) == WcxAnsi.OffExArcName, $"tHeaderDataEx.ArcName — inline char* массив на {WcxAnsi.ExNameBytes} байт (0)");
        Check(Off(exBase, ex.FileName) == WcxAnsi.OffExFileName, $"tHeaderDataEx.FileName на {WcxAnsi.OffExFileName}");
        Check(Off(exBase, &ex.Flags) == WcxAnsi.OffExFlags, $"tHeaderDataEx.Flags на {WcxAnsi.OffExFlags}");
        Check(Off(exBase, &ex.PackSize) == WcxAnsi.OffExPackSize, $"tHeaderDataEx.PackSize (uint) на {WcxAnsi.OffExPackSize}");
        Check(Off(exBase, &ex.PackSizeHigh) == WcxAnsi.OffExPackSizeHigh, $"tHeaderDataEx.PackSizeHigh на {WcxAnsi.OffExPackSizeHigh}");
        Check(Off(exBase, &ex.UnpSize) == WcxAnsi.OffExUnpSize, $"tHeaderDataEx.UnpSize на {WcxAnsi.OffExUnpSize}");
        Check(Off(exBase, &ex.UnpSizeHigh) == WcxAnsi.OffExUnpSizeHigh, $"tHeaderDataEx.UnpSizeHigh на {WcxAnsi.OffExUnpSizeHigh}");
        Check(Off(exBase, &ex.HostOS) == WcxAnsi.OffExHostOS, $"tHeaderDataEx.HostOS на {WcxAnsi.OffExHostOS}");
        Check(Off(exBase, &ex.FileCRC) == WcxAnsi.OffExFileCRC, $"tHeaderDataEx.FileCRC на {WcxAnsi.OffExFileCRC}");
        Check(Off(exBase, &ex.FileTime) == WcxAnsi.OffExFileTime, $"tHeaderDataEx.FileTime на {WcxAnsi.OffExFileTime}");
        Check(Off(exBase, &ex.UnpVer) == WcxAnsi.OffExUnpVer, $"tHeaderDataEx.UnpVer на {WcxAnsi.OffExUnpVer}");
        Check(Off(exBase, &ex.Method) == WcxAnsi.OffExMethod, $"tHeaderDataEx.Method на {WcxAnsi.OffExMethod}");
        Check(Off(exBase, &ex.FileAttr) == WcxAnsi.OffExFileAttr, $"tHeaderDataEx.FileAttr на {WcxAnsi.OffExFileAttr}");
        Check(Off(exBase, &ex.CmtBuf) == WcxAnsi.OffExCmtBuf, $"tHeaderDataEx.CmtBuf на {WcxAnsi.OffExCmtBuf} (после int FileAttr на 2088 — выравнивание по 8, хвост 2092..2095)");
        Check(Off(exBase, &ex.CmtBufSize) == WcxAnsi.OffExCmtBufSize, $"tHeaderDataEx.CmtBufSize на {WcxAnsi.OffExCmtBufSize}");
        Check(Off(exBase, &ex.CmtSize) == WcxAnsi.OffExCmtSize, $"tHeaderDataEx.CmtSize на {WcxAnsi.OffExCmtSize}");
        Check(Off(exBase, &ex.CmtState) == WcxAnsi.OffExCmtState, $"tHeaderDataEx.CmtState на {WcxAnsi.OffExCmtState}");
        Check(Off(exBase, ex.Reserved) == WcxAnsi.OffExReserved,
            $"tHeaderDataEx.Reserved на {Off(exBase, ex.Reserved)} — сразу после CmtState (2112..2115): byte-массив выравнивания не просит");
        Check(Off(exBase, ex.Reserved) + WcxAnsi.ExReservedBytes + 4 == WcxAnsi.SizeOfHeaderEx
            && WcxAnsi.SizeOfHeaderEx % 8 == 0,
            $"ANSI Reserved (1024 байта) заканчивается на {Off(exBase, ex.Reserved) + WcxAnsi.ExReservedBytes}, затем 4 байта хвостового выравнивания → sizeof = {WcxAnsi.SizeOfHeaderEx}");
        Check(Wcx.SizeOfHeader - WcxAnsi.SizeOfHeaderEx == 2048,
            $"ANSI tHeaderDataEx ({WcxAnsi.SizeOfHeaderEx}) короче W-версии ({Wcx.SizeOfHeader}) ровно на 2048: два имени по 1024 символа заменены двумя по 1024 байта");
        Check(WcxAnsi.SizeOfHeaderEx - WcxAnsi.SizeOfHeader == 2560,
            $"ANSI tHeaderDataEx (3144) длиннее tHeaderData (584) на 2560: имена длиннее на 2×764 (1528), плюс PackSizeHigh/UnpSizeHigh (8), плюс Reserved (1024); выравнивания в обеих структурах одинаковые — по 8 байт");

        Console.WriteLine($"  измеренные смещения ANSI tHeaderDataEx: ArcName 0, FileName {Off(exBase, ex.FileName)}, CmtBuf {Off(exBase, &ex.CmtBuf)}, Reserved {Off(exBase, ex.Reserved)}..{Off(exBase, ex.Reserved) + WcxAnsi.ExReservedBytes - 1}, sizeof {sizeof(tHeaderDataEx)}");

        Console.WriteLine();
    }

    // ──────────────────── ANSI-ветка «первого издания» ────────────────────

    private sealed record AnsiArcEntry(ArcEntry Entry, byte[] RawName, byte[] RawArchive);

    /// <summary>
    /// Полный цикл «первого издания» (OpenArchive / ReadHeader / ReadHeaderEx / ProcessFile /
    /// SetProcessDataProc / SetChangeVolProc без суффикса W). Все ANSI-папки лежат внутри --workdir,
    /// поэтому юникод-проходы в другой папке не затрагиваются.
    /// </summary>
    private static void RunAnsiBranch(Plugin plugin, Options options, List<ArcEntry> unicodeListing)
    {
        Section($"ANSI-ветка «первого издания»: имя в кодовой странице системы (ACP = {AnsiCodePage.CurrentCodePage()})");
        Console.WriteLine($"  рабочие папки ANSI-проходов: {options.AnsiWorkDir}");
        Console.WriteLine();

        List<AnsiArcEntry> ansiListing = ReadAnsiListing(plugin, options.Container);
        Check(ansiListing.Count == unicodeListing.Count,
            $"ANSI ReadHeaderEx вернул столько же записей, что ReadHeaderExW ({ansiListing.Count} против {unicodeListing.Count})");

        RunAnsiLayoutEvidence(plugin, options.Container);

        List<ArcEntry> ansiHeaderListing = RunAnsiReadHeader(plugin, options.Container);
        CompareAnsWithUnicode(ansiListing, ansiHeaderListing, unicodeListing);

        if (ansiListing.Count > 0)
            CompareListing(ansiListing.Select(e => e.Entry).ToList(), Snapshot.Collect(options.Source),
                "ANSI-сверка списка контейнера с оригиналом на диске");

        RunAnsiExtract(plugin, options, ansiListing);
        RunAnsiVerify(plugin, options, unicodeListing);
        RunAnsiAbort(plugin, options);
        RunAnsiBrokenInputs(plugin, options);
    }

    /// <summary>ANSI-список: OpenArchive(PK_OM_LIST) → ReadHeaderEx + PK_SKIP по кругу → CloseArchive.</summary>
    private static List<AnsiArcEntry> ReadAnsiListing(Plugin plugin, string container, bool verbose = true)
    {
        Section("проход 7: ANSI-список (PK_OM_LIST) — OpenArchive/ReadHeaderEx без суффикса W");

        List<AnsiArcEntry> entries = new();
        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(container);
        byte[] comment = new byte[4096];
        bool offsetsMatched = true;
        bool reservedCleared = true;
        bool namesAreCodePage = true;
        bool arcNameOk = true;
        bool endedWithEndArchive = false;
        int skips = 0;
        List<string> badNames = new();

        fixed (byte* archiveName = ansiArchive)
        fixed (byte* commentBuffer = comment)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;      // char* ArcName, как в W-версии
            data.OpenMode = Wcx.PK_OM_LIST;
            data.CmtBuf = commentBuffer;
            data.CmtBufSize = comment.Length;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive(PK_OM_LIST) вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return entries;
            }

            Check(handle != 0 && data.OpenResult == 0, $"OpenArchive(PK_OM_LIST): handle выдан, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
            Check(data.CmtState is 0 or 1, $"ANSI CmtState в допустимых значениях 0/1 (комментария нет), получено {data.CmtState}");

            plugin.SetChangeVolProc(handle, 0);

            while (true)
            {
                tHeaderDataEx header = default;
                header.CmtBuf = null;        // SDK: Cmt* в заголовке разрешено обнулить
                header.CmtBufSize = 0;

                int code = plugin.ReadHeaderEx(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                {
                    endedWithEndArchive = true;
                    break;
                }

                if (code != 0)
                {
                    Fail($"ANSI ReadHeaderEx вернул {Wcx.ErrorName(code)} после {entries.Count} записей");
                    break;
                }

                byte* raw = (byte*)&header;
                offsetsMatched &= RawAnsiExFieldsMatchTypedFields(raw, &header);
                reservedCleared &= IsZero(raw + WcxAnsi.OffExReserved, WcxAnsi.ExReservedBytes);

                byte* rawName = raw + WcxAnsi.OffExFileName;
                string name = AnsiCodePage.FromAnsi(rawName, WcxAnsi.ExNameBytes);
                byte[] rawNameBytes = AnsiCodePage.SnapshotBytes(rawName, WcxAnsi.ExNameBytes);
                byte[] reencoded = AnsiCodePage.ToAnsi(name);
                if (!BytesEqual(reencoded, rawNameBytes))
                    badNames.Add($"{Quote(name)}: в заголовке {Convert.ToHexString(rawNameBytes)}, CP_ACP даёт {Convert.ToHexString(reencoded)}");
                namesAreCodePage &= reencoded.Length == rawNameBytes.Length;

                byte* rawArchive = raw + WcxAnsi.OffExArcName;
                arcNameOk &= AnsiCodePage.FromAnsi(rawArchive, WcxAnsi.ExNameBytes) == container;

                entries.Add(new AnsiArcEntry(
                    new ArcEntry(
                        name,
                        Combine(ReadUInt32(raw + WcxAnsi.OffExUnpSizeHigh), ReadUInt32(raw + WcxAnsi.OffExUnpSize)),
                        Combine(ReadUInt32(raw + WcxAnsi.OffExPackSizeHigh), ReadUInt32(raw + WcxAnsi.OffExPackSize)),
                        ReadInt32(raw + WcxAnsi.OffExFileAttr),
                        ReadInt32(raw + WcxAnsi.OffExFileTime),
                        (ReadInt32(raw + WcxAnsi.OffExFileAttr) & Wcx.FA_DIRECTORY) != 0),
                    rawNameBytes,
                    AnsiCodePage.SnapshotBytes(rawArchive, WcxAnsi.ExNameBytes)));

                int skipped = plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null);
                if (skipped != 0)
                {
                    Fail($"ANSI ProcessFile(PK_SKIP) вернул {Wcx.ErrorName(skipped)}");
                    break;
                }

                skips++;
            }

            int closed = plugin.CloseArchive(handle);
            Check(closed == 0, $"ANSI CloseArchive == 0, получено {Wcx.ErrorName(closed)}");
            Check(endedWithEndArchive, $"ANSI ReadHeaderEx закончил список кодом E_END_ARCHIVE после {entries.Count} записей");
            Check(skips == entries.Count, $"ANSI ProcessFile(PK_SKIP) вызван для каждой прочитанной записи ({skips} из {entries.Count}) и вернулся 0");
            Check(offsetsMatched, $"значения в ANSI tHeaderDataEx (3144 байта) видны на смещениях x64 во всех {entries.Count} записях");
            Check(reservedCleared, $"ANSI Reserved обнулён во всех {entries.Count} записях");
            Check(namesAreCodePage && badNames.Count == 0, $"ANSI-имя в заголовке побайтово равно CP_ACP-кодированию этого же имени (расхождений {badNames.Count}: {Join(badNames)})");
            Check(arcNameOk, $"ANSI ArcName = путь контейнера в CP_ACP во всех {entries.Count} записях");
        }

        if (verbose)
        {
            foreach (AnsiArcEntry entry in entries)
            {
                string hex = entry.RawName.Any(b => b >= 0x80) ? $"  байты {Convert.ToHexString(entry.RawName)}" : string.Empty;
                Console.WriteLine($"  {(entry.Entry.IsDirectory ? "папка" : "файл ")}  {Quote(entry.Entry.Name),-46} {entry.Entry.UnpSize,12}  attrs 0x{entry.Entry.FileAttr:X2}{hex}");
            }

            Console.WriteLine();
        }

        return entries;
    }

    private static bool RawAnsiExFieldsMatchTypedFields(byte* raw, tHeaderDataEx* header)
    {
        string rawName = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes);
        string rawArchive = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExArcName, WcxAnsi.ExNameBytes);

        // Имя поля fixed byte[1024] в небезопасном коде уже является указателем на начало буфера.
        string typedName = AnsiCodePage.FromAnsi(header->FileName, WcxAnsi.ExNameBytes);
        string typedArchive = AnsiCodePage.FromAnsi(header->ArcName, WcxAnsi.ExNameBytes);

        return rawName == typedName
            && rawArchive == typedArchive
            && ReadInt32(raw + WcxAnsi.OffExFileAttr) == header->FileAttr
            && ReadInt32(raw + WcxAnsi.OffExFlags) == header->Flags
            && ReadInt32(raw + WcxAnsi.OffExFileTime) == header->FileTime
            && ReadInt32(raw + WcxAnsi.OffExHostOS) == header->HostOS
            && ReadInt32(raw + WcxAnsi.OffExFileCRC) == header->FileCRC
            && ReadUInt32(raw + WcxAnsi.OffExUnpSize) == header->UnpSize
            && ReadUInt32(raw + WcxAnsi.OffExUnpSizeHigh) == header->UnpSizeHigh
            && ReadUInt32(raw + WcxAnsi.OffExPackSize) == header->PackSize
            && ReadUInt32(raw + WcxAnsi.OffExPackSizeHigh) == header->PackSizeHigh
            && ReadInt32(raw + WcxAnsi.OffExCmtBufSize) == header->CmtBufSize
            && ReadInt32(raw + WcxAnsi.OffExCmtSize) == header->CmtSize
            && ReadInt32(raw + WcxAnsi.OffExCmtState) == header->CmtState;
    }

    private static bool RawAnsiFieldsMatchTypedFields(byte* raw, tHeaderData* header)
    {
        string rawName = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffFileName, WcxAnsi.NameBytes);
        string rawArchive = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffArcName, WcxAnsi.NameBytes);
        string typedName = AnsiCodePage.FromAnsi(header->FileName, WcxAnsi.NameBytes);
        string typedArchive = AnsiCodePage.FromAnsi(header->ArcName, WcxAnsi.NameBytes);

        return rawName == typedName
            && rawArchive == typedArchive
            && ReadInt32(raw + WcxAnsi.OffFileAttr) == header->FileAttr
            && ReadInt32(raw + WcxAnsi.OffFlags) == header->Flags
            && ReadInt32(raw + WcxAnsi.OffFileTime) == header->FileTime
            && ReadInt32(raw + WcxAnsi.OffHostOS) == header->HostOS
            && ReadInt32(raw + WcxAnsi.OffFileCRC) == header->FileCRC
            && ReadUInt32(raw + WcxAnsi.OffUnpSize) == unchecked((uint)header->UnpSize)
            && ReadUInt32(raw + WcxAnsi.OffPackSize) == unchecked((uint)header->PackSize)
            && ReadInt32(raw + WcxAnsi.OffCmtBufSize) == header->CmtBufSize
            && ReadInt32(raw + WcxAnsi.OffCmtSize) == header->CmtSize
            && ReadInt32(raw + WcxAnsi.OffCmtState) == header->CmtState;
    }

    /// <summary>
    /// Проверка раскладки по факту: поля, которые плагин сам заполняет, предзаполнены значением
    /// 0xAAAAAAAA, а сразу за концом структуры лежит гард-зона из тех же 0xAA. Так видно, какие
    /// именно смещения и какой размер реально использует нативный код плагина, а не только то,
    /// что харнесс так же объявил структуру.
    /// </summary>
    private static void RunAnsiLayoutEvidence(Plugin plugin, string container)
    {
        Section("проход 8: фактические смещения ANSI-структур (sentinel 0xAA + гард-зона 64 байта)");

        const int sentinel = unchecked((int)0xAAAAAAAA);
        const int guard = 64;
        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(container);
        byte[] comment = new byte[4096];

        nint handle;

        byte[] openBlock = new byte[WcxAnsi.SizeOfOpenData + guard];
        FillSentinel(openBlock);

        fixed (byte* block = openBlock)
        fixed (byte* archiveName = ansiArchive)
        fixed (byte* commentBuffer = comment)
        {
            tOpenArchiveData* data = (tOpenArchiveData*)block;
            data->ArcName = archiveName;
            data->OpenMode = Wcx.PK_OM_LIST;
            data->OpenResult = sentinel;
            data->CmtBuf = commentBuffer;
            data->CmtBufSize = comment.Length;
            data->CmtSize = sentinel;
            data->CmtState = sentinel;

            handle = plugin.OpenArchive(data);

            Check(handle != 0, $"ANSI OpenArchive открыл контейнер: имя читается как char* в смещении 0 (OpenResult = {Wcx.ErrorName(data->OpenResult)})");
            Check(ReadInt32(block + WcxAnsi.OffOpenResult) == 0, $"OpenArchive записал OpenResult в смещение {WcxAnsi.OffOpenResult}: было 0x{sentinel:X8}, стало {Hex32(ReadUInt32(block + WcxAnsi.OffOpenResult))}");
            Check(ReadInt32(block + WcxAnsi.OffOpenCmtSize) == 0, $"CmtSize записан в смещение {WcxAnsi.OffOpenCmtSize}: стало {Hex32(ReadUInt32(block + WcxAnsi.OffOpenCmtSize))}");
            Check(ReadInt32(block + WcxAnsi.OffOpenCmtState) == 1, $"CmtState записан в смещение {WcxAnsi.OffOpenCmtState}: стало {Hex32(ReadUInt32(block + WcxAnsi.OffOpenCmtState))}");
            Check(ReadInt32(block + WcxAnsi.OffOpenMode) == Wcx.PK_OM_LIST,
                $"OpenMode (PK_OM_LIST = 0) читается плагином в смещении {WcxAnsi.OffOpenMode}: контейнер открыт именно в режиме списка");
            Check(AllSentinel(block, WcxAnsi.OffOpenCmtState + 4, WcxAnsi.SizeOfOpenData),
                $"4 байта хвостового выравнивания после CmtState ({WcxAnsi.OffOpenCmtState + 4}..{WcxAnsi.SizeOfOpenData - 1}) остались sentinel 0xAA → плагин не пишет за пределы CmtState");
            Check(AllSentinel(block, WcxAnsi.SizeOfOpenData, WcxAnsi.SizeOfOpenData + guard),
                $"гард-зона за {WcxAnsi.SizeOfOpenData} байтом не тронута → sizeof(tOpenArchiveData) у плагина не больше {WcxAnsi.SizeOfOpenData}");
        }

        if (handle == 0)
        {
            Fail("без открытого ANSI-handle нельзя проверить смещения ANSI-заголовков");
            Console.WriteLine();
            return;
        }

        byte[] exBlock = new byte[WcxAnsi.SizeOfHeaderEx + guard];
        FillSentinel(exBlock);

        fixed (byte* block = exBlock)
        {
            tHeaderDataEx* header = (tHeaderDataEx*)block;
            header->CmtBuf = null;
            header->CmtBufSize = 0;

            int code = plugin.ReadHeaderEx(handle, header);
            Check(code == 0, $"ANSI ReadHeaderEx вернул 0 для sentinel-буфера, получено {Wcx.ErrorName(code)}");
            Check(code == 0 && IsZero(block + WcxAnsi.SizeOfHeaderEx - 16, 16),
                $"последние 16 байт tHeaderDataEx обнулены (0xAA там не осталось) → плагин обнулил не меньше {WcxAnsi.SizeOfHeaderEx} байт");
            Check(code == 0 && AllSentinel(block, WcxAnsi.SizeOfHeaderEx, WcxAnsi.SizeOfHeaderEx + guard),
                $"гард-зона за {WcxAnsi.SizeOfHeaderEx} байтом не тронута → sizeof(tHeaderDataEx) у плагина не больше {WcxAnsi.SizeOfHeaderEx}");
            Check(code == 0 && IsZero(block + WcxAnsi.OffExArcName + 1008, 16), $"ANSI ArcName[1024] обнулён до последнего байта буфера (смещение {WcxAnsi.OffExArcName}..{WcxAnsi.OffExArcName + 1023})");
            Check(code == 0 && IsZero(block + WcxAnsi.OffExFileName + 1008, 16), $"ANSI FileName[1024] обнулён до последнего байта буфера (смещение {WcxAnsi.OffExFileName}..{WcxAnsi.OffExFileName + 1023})");
            Check(code == 0 && ReadInt32(block + WcxAnsi.OffExFileAttr) != sentinel,
                $"FileAttr читается в смещении {WcxAnsi.OffExFileAttr}: sentinel 0x{sentinel:X8} заменён на {Hex32(ReadUInt32(block + WcxAnsi.OffExFileAttr))}");
            Check(code == 0 && plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null) == 0, "ANSI ProcessFile(PK_SKIP) после sentinel-чтения ReadHeaderEx принята (0)");
        }

        byte[] headerBlock = new byte[WcxAnsi.SizeOfHeader + guard];
        FillSentinel(headerBlock);

        fixed (byte* block = headerBlock)
        {
            tHeaderData* header = (tHeaderData*)block;

            int code = plugin.ReadHeader(handle, header);
            Check(code == 0, $"ANSI ReadHeader вернул 0 для sentinel-буфера, получено {Wcx.ErrorName(code)}");
            Check(code == 0 && IsZero(block + WcxAnsi.SizeOfHeader - 16, 16),
                $"последние 16 байт tHeaderData обнулены → плагин обнулил не меньше {WcxAnsi.SizeOfHeader} байт");
            Check(code == 0 && AllSentinel(block, WcxAnsi.SizeOfHeader, WcxAnsi.SizeOfHeader + guard),
                $"гард-зона за {WcxAnsi.SizeOfHeader} байтом не тронута → sizeof(tHeaderData) у плагина не больше {WcxAnsi.SizeOfHeader}");
            Check(code == 0 && IsZero(block + WcxAnsi.OffArcName + WcxAnsi.NameBytes - 16, 16), $"ANSI ArcName[260] обнулён до последнего байта буфера");
            Check(code == 0 && IsZero(block + WcxAnsi.OffFileName + WcxAnsi.NameBytes - 16, 16), $"ANSI FileName[260] обнулён до последнего байта буфера");
            Check(code == 0 && ReadInt32(block + WcxAnsi.OffFileAttr) != sentinel,
                $"FileAttr читается в смещении {WcxAnsi.OffFileAttr}: sentinel 0x{sentinel:X8} заменён на {Hex32(ReadUInt32(block + WcxAnsi.OffFileAttr))}");
            Check(code == 0 && plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null) == 0, "ANSI ProcessFile(PK_SKIP) после sentinel-чтения ReadHeader принята (0)");
        }

        Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после sentinel-проверок == 0");
        Console.WriteLine();
    }

    /// <summary>ANSI ReadHeader: та же запись, но tHeaderData (584 байта, размеры только 32 бита).</summary>
    private static List<ArcEntry> RunAnsiReadHeader(Plugin plugin, string container)
    {
        Section("проход 9: ANSI ReadHeader (tHeaderData, «первое издание», PackSize/UnpSize по 32 бита)");

        List<ArcEntry> entries = new();
        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(container);
        bool offsetsMatched = true;
        bool endedWithEndArchive = false;

        fixed (byte* archiveName = ansiArchive)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive(PK_OM_LIST) для ReadHeader вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return entries;
            }

            while (true)
            {
                tHeaderData header = default;
                int code = plugin.ReadHeader(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                {
                    endedWithEndArchive = true;
                    break;
                }

                if (code != 0)
                {
                    Fail($"ANSI ReadHeader вернул {Wcx.ErrorName(code)} после {entries.Count} записей");
                    break;
                }

                byte* raw = (byte*)&header;
                offsetsMatched &= RawAnsiFieldsMatchTypedFields(raw, &header);

                entries.Add(new ArcEntry(
                    AnsiCodePage.FromAnsi(raw + WcxAnsi.OffFileName, WcxAnsi.NameBytes),
                    ReadUInt32(raw + WcxAnsi.OffUnpSize),
                    ReadUInt32(raw + WcxAnsi.OffPackSize),
                    ReadInt32(raw + WcxAnsi.OffFileAttr),
                    ReadInt32(raw + WcxAnsi.OffFileTime),
                    (ReadInt32(raw + WcxAnsi.OffFileAttr) & Wcx.FA_DIRECTORY) != 0));

                plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null);
            }

            Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после ReadHeader == 0");
        }

        Check(endedWithEndArchive, $"ANSI ReadHeader закончил список кодом E_END_ARCHIVE после {entries.Count} записей");
        Check(offsetsMatched, $"значения в tHeaderData (584 байта) видны на смещениях x64 во всех {entries.Count} записях");

        foreach (ArcEntry entry in entries)
            Console.WriteLine($"  {(entry.IsDirectory ? "папка" : "файл ")}  {Quote(entry.Name),-46} Unp {entry.UnpSize,10}  Pack {entry.PackSize,10}  attrs 0x{entry.FileAttr:X2}");

        Console.WriteLine();
        return entries;
    }

    private static void CompareAnsWithUnicode(List<AnsiArcEntry> ansiEx, List<ArcEntry> ansiHeader, List<ArcEntry> unicode)
    {
        Section("ANSI против Unicode: построчная сверка листингов одного контейнера");

        int lines = Math.Min(unicode.Count, Math.Min(ansiEx.Count, ansiHeader.Count));
        List<string> nameDiff = new();
        List<string> headerNameDiff = new();
        List<string> sizeDiff = new();
        List<string> attrDiff = new();
        List<string> timeDiff = new();
        List<string> truncated = new();

        for (int i = 0; i < lines; i++)
        {
            ArcEntry w = unicode[i];
            ArcEntry a = ansiEx[i].Entry;
            ArcEntry h = ansiHeader[i];

            if (a.Name != w.Name)
                nameDiff.Add($"строка {i}: ANSI {Quote(a.Name)} против W {Quote(w.Name)}");
            if (h.Name != a.Name)
                headerNameDiff.Add($"строка {i}: ReadHeader {Quote(h.Name)} против ReadHeaderEx {Quote(a.Name)}");
            if (a.UnpSize != w.UnpSize || a.PackSize != w.PackSize)
                sizeDiff.Add($"{Quote(w.Name)}: ANSI {a.UnpSize}/{a.PackSize} против W {w.UnpSize}/{w.PackSize}");
            if (a.FileAttr != w.FileAttr || h.FileAttr != w.FileAttr)
                attrDiff.Add($"{Quote(w.Name)}: ANSI 0x{a.FileAttr:X2}/0x{h.FileAttr:X2} против W 0x{w.FileAttr:X2}");
            if (a.FileTime != w.FileTime || h.FileTime != w.FileTime)
                timeDiff.Add($"{Quote(w.Name)}: ANSI 0x{a.FileTime:X8}/0x{h.FileTime:X8} против W 0x{w.FileTime:X8}");
            if (h.UnpSize != (w.UnpSize & 0xFFFFFFFFL) || h.PackSize != (w.PackSize & 0xFFFFFFFFL))
                truncated.Add($"{Quote(w.Name)}: 32-битный ReadHeader {h.UnpSize}/{h.PackSize}, младшие 32 бита W-размера {(w.UnpSize & 0xFFFFFFFFL)}/{(w.PackSize & 0xFFFFFFFFL)}");

            Console.WriteLine($"  {(w.IsDirectory ? "папка" : "файл ")}  ANSI {Quote(a.Name),-44} {a.UnpSize,10} 0x{a.FileAttr:X2} | W {Quote(w.Name),-44} {w.UnpSize,10} 0x{w.FileAttr:X2}");
        }

        Check(nameDiff.Count == 0, $"ANSI ReadHeaderEx отдаёт те же имена, что ReadHeaderExW ({nameDiff.Count} расхождений: {Join(nameDiff)})");
        Check(headerNameDiff.Count == 0, $"ANSI ReadHeader отдаёт те же имена, что ANSI ReadHeaderEx ({headerNameDiff.Count} расхождений: {Join(headerNameDiff)})");
        Check(sizeDiff.Count == 0, $"ANSI ReadHeaderEx отдаёт те же размеры, что ReadHeaderExW ({sizeDiff.Count} расхождений: {Join(sizeDiff)})");
        Check(attrDiff.Count == 0, $"ANSI отдаёт те же FileAttr, что W-ветка ({attrDiff.Count} расхождений: {Join(attrDiff)})");
        Check(timeDiff.Count == 0, $"ANSI отдаёт то же DOS-время, что W-ветка ({timeDiff.Count} расхождений: {Join(timeDiff)})");
        Check(truncated.Count == 0, $"32-битные UnpSize/PackSize в tHeaderData равны младшим 32 битам реальных размеров ({truncated.Count} расхождений: {Join(truncated)})");

        List<string> trailingSlash = ansiEx.Where(e => !e.Entry.IsDirectory && e.Entry.Name.EndsWith('\\')).Select(e => e.Entry.Name).ToList();
        Check(trailingSlash.Count == 0, $"у файловых ANSI-имён нет завершающего «\\\\» (найдено {trailingSlash.Count}: {Join(trailingSlash)})");

        int ansiDirs = ansiEx.Count(e => e.Entry.IsDirectory);
        int wrongDirAttr = ansiEx.Count(e => e.Entry.IsDirectory && (e.Entry.FileAttr & Wcx.FA_DIRECTORY) == 0)
            + ansiEx.Count(e => !e.Entry.IsDirectory && (e.Entry.FileAttr & Wcx.FA_DIRECTORY) != 0);
        Check(ansiDirs > 0 && wrongDirAttr == 0, $"ANSI-папки (их {ansiDirs}) помечены 0x10, у файлов 0x10 нет (ошибок {wrongDirAttr})");

        List<string> nonAscii = unicode.Where(e => e.Name.Any(c => c > 127)).Select(e => e.Name).ToList();
        List<string> lostUnicodeNames = nonAscii.Where(name => !ansiEx.Any(e => e.Entry.Name == name)).ToList();
        Check(nonAscii.Count > 0 && lostUnicodeNames.Count == 0, $"нелатинские имена ({nonAscii.Count}: {Join(nonAscii)}) декодированы из ANSI-байтов без искажений (потеряно {lostUnicodeNames.Count})");

        List<string> spaced = unicode.Where(e => e.Name.Contains(' ')).Select(e => e.Name).ToList();
        List<string> lostSpaced = spaced.Where(name => !ansiEx.Any(e => e.Entry.Name == name)).ToList();
        Check(spaced.Count > 0 && lostSpaced.Count == 0, $"имена с пробелами ({spaced.Count}: {Join(spaced)}) дошли из ANSI-листинга целыми (потеряно {lostSpaced.Count})");

        Check(ansiEx.All(e => e.RawArchive.Length > 0), $"ANSI ArcName заполнен во всех {ansiEx.Count} записях (пример байтов: {Convert.ToHexString(ansiEx[0].RawArchive)})");
        Console.WriteLine();
    }

    /// <summary>ANSI-извлечение: OpenArchive(PK_OM_EXTRACT) → ReadHeaderEx → ProcessFile(PK_EXTRACT) с ANSI-путями.</summary>
    private static void RunAnsiExtract(Plugin plugin, Options options, List<AnsiArcEntry> ansiListing)
    {
        Section("проход 10: ANSI-извлечение (PK_OM_EXTRACT) — ProcessFile без суффикса W, ANSI-пути и ANSI-колбэк");

        string target = Path.Combine(options.AnsiWorkDir, "ansi-extract");
        ResetFolder(target);

        // Отдельная папка: вариант «полный путь в DestName, DestPath = NULL» не должен мешать
        // сверке основного ANSI-извлечения с оригиналом.
        string variantDir = Path.Combine(options.AnsiWorkDir, "ansi-variants");
        ResetFolder(variantDir);
        string variantFile = Path.Combine(variantDir, "полный путь в ansi.txt");

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(options.Container);
        byte[] ansiTarget = AnsiCodePage.ToAnsiZ(target);
        byte[] comment = new byte[4096];

        List<ArcEntry> extractPass = new();
        int extractErrors = 0;
        string firstExtractError = string.Empty;
        bool endedWithEndArchive = false;

        AnsiProgress? progress = null;
        string callbackProblem = string.Empty;
        try
        {
            progress = new AnsiProgress();
        }
        catch (Exception ex)
        {
            callbackProblem = $"{ex.GetType().Name}: {ex.Message}";
        }

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
                Fail($"OpenArchive(PK_OM_EXTRACT) вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            Check(handle != 0 && data.OpenResult == 0, $"OpenArchive(PK_OM_EXTRACT): handle выдан, OpenResult = {Wcx.ErrorName(data.OpenResult)}");

            plugin.SetChangeVolProc(handle, 0);

            if (progress is not null)
            {
                int setProc = plugin.SetProcessDataProc(handle, progress.Handle);
                Check(setProc == 0, $"SetProcessDataProc (ANSI) принял нативный указатель колбэка и вернул {Wcx.ErrorName(setProc)}");
            }
            else
            {
                Skipped($"ANSI-колбэк передать не удалось ({callbackProblem}) — проверки tProcessDataProc пропущены");
            }

            while (true)
            {
                tHeaderDataEx header = default;
                header.CmtBuf = commentBuffer;
                header.CmtBufSize = comment.Length;

                int code = plugin.ReadHeaderEx(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                {
                    endedWithEndArchive = true;
                    break;
                }

                if (code != 0)
                {
                    Fail($"ANSI ReadHeaderEx в режиме извлечения вернул {Wcx.ErrorName(code)} после {extractPass.Count} записей");
                    break;
                }

                byte* raw = (byte*)&header;
                ArcEntry entry = new(
                    AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes),
                    Combine(ReadUInt32(raw + WcxAnsi.OffExUnpSizeHigh), ReadUInt32(raw + WcxAnsi.OffExUnpSize)),
                    Combine(ReadUInt32(raw + WcxAnsi.OffExPackSizeHigh), ReadUInt32(raw + WcxAnsi.OffExPackSize)),
                    ReadInt32(raw + WcxAnsi.OffExFileAttr),
                    ReadInt32(raw + WcxAnsi.OffExFileTime),
                    (ReadInt32(raw + WcxAnsi.OffExFileAttr) & Wcx.FA_DIRECTORY) != 0);
                extractPass.Add(entry);

                byte[] ansiName = AnsiCodePage.ToAnsiZ(entry.Name);
                fixed (byte* entryName = ansiName)
                {
                    int extracted = plugin.ProcessFile(handle, Wcx.PK_EXTRACT, targetPath, entryName);
                    if (extracted != 0)
                    {
                        extractErrors++;
                        if (firstExtractError.Length == 0)
                            firstExtractError = $"{Quote(entry.Name)} → {Wcx.ErrorName(extracted)}";
                    }
                }
            }

            Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после извлечения == 0");
            Check(endedWithEndArchive, $"ANSI-проход извлечения закончен кодом E_END_ARCHIVE ({extractPass.Count} записей прочитано)");
            Check(extractErrors == 0, $"все {extractPass.Count} записей извлечены ANSI-ProcessFile(PK_EXTRACT) без ошибок (ошибок {extractErrors}: {firstExtractError})");
        }

        List<ArcEntry> files = extractPass.Where(e => !e.IsDirectory).ToList();

        if (progress is not null)
        {
            Check(progress.Calls == files.Count, $"ANSI tProcessDataProc вызвана плагином ровно по разу на каждый файл ({progress.Calls} вызовов на {files.Count} файлов, {progress.Bytes} байт)");

            int pairs = Math.Min(progress.Names.Count, files.Count);
            List<string> wrongNames = new();
            List<string> wrongSizes = new();
            List<string> wrongBytes = new();
            for (int i = 0; i < pairs; i++)
            {
                if (progress.Names[i] != files[i].Name)
                    wrongNames.Add($"{Quote(progress.Names[i])} ≠ {Quote(files[i].Name)}");
                if (progress.Sizes[i] != files[i].UnpSize)
                    wrongSizes.Add($"{Quote(files[i].Name)}: {progress.Sizes[i]} ≠ {files[i].UnpSize}");
                if (!BytesEqual(AnsiCodePage.ToAnsi(files[i].Name), progress.RawNames[i]))
                    wrongBytes.Add($"{Quote(files[i].Name)}: колбэк получил {Convert.ToHexString(progress.RawNames[i])}, ожидалось {Convert.ToHexString(AnsiCodePage.ToAnsi(files[i].Name))}");
            }

            Check(wrongNames.Count == 0 && progress.Names.Count == files.Count,
                $"ANSI-имена из tProcessDataProc декодируются из кодовой страницы системы ровно в имена контейнера (расхождений {wrongNames.Count}: {Join(wrongNames)})");
            Check(wrongSizes.Count == 0, $"размеры в ANSI tProcessDataProc совпадают с размерами записей (расхождений {wrongSizes.Count}: {Join(wrongSizes)})");
            Check(wrongBytes.Count == 0 && progress.RawNames.Count == files.Count,
                $"ANSI tProcessDataProc приняла побайтово те же CP_ACP-байты имени (пример: {Join(progress.RawNames.Take(3).Select(Convert.ToHexString))}; расхождений {wrongBytes.Count}: {Join(wrongBytes)})");
        }

        Check(extractPass.Count == ansiListing.Count && extractPass.Zip(ansiListing).All(p => p.First.Name == p.Second.Entry.Name),
            $"ANSI-режимы PK_OM_LIST и PK_OM_EXTRACT дали один и тот же список ({extractPass.Count} против {ansiListing.Count})");

        Snapshot original = Snapshot.Collect(options.Source);
        Snapshot afterExtract = Snapshot.Collect(target);

        List<string> lost = original.Files.Keys.Where(name => !afterExtract.Files.ContainsKey(name)).ToList();
        List<string> extra = afterExtract.Files.Keys.Where(name => !original.Files.ContainsKey(name)).ToList();
        List<string> badHash = new();
        List<string> zeroLength = new();
        foreach (KeyValuePair<string, (long Size, string Sha256)> file in afterExtract.Files)
        {
            if (!original.Files.TryGetValue(file.Key, out (long Size, string Sha256) source))
                continue;
            if (source.Sha256 != file.Value.Sha256)
                badHash.Add($"{Quote(file.Key)}: SHA-256 извлечённого файла не совпал");
            if (file.Value.Size == 0 && source.Size != 0)
                zeroLength.Add(Quote(file.Key));
        }

        Check(lost.Count == 0, $"ANSI-извлечение вернуло все {original.Files.Count} файлов (потеряно {lost.Count}: {Join(lost)})");
        Check(extra.Count == 0, $"ANSI-извлечение не добавило лишних файлов (лишних {extra.Count}: {Join(extra)})");
        Check(badHash.Count == 0, $"ANSI-извлечение байт в байт: SHA-256 {afterExtract.Files.Count} файлов совпал с SHA-256 оригинала (расхождений {badHash.Count}: {Join(badHash)})");
        Check(zeroLength.Count == 0, $"ANSI-извлечение не обнулило непустые файлы (обнулено {zeroLength.Count}: {Join(zeroLength)})");

        List<string> missingDirs = original.Directories.Where(dir => !afterExtract.Directories.Contains(dir)).ToList();
        Check(missingDirs.Count == 0, $"ANSI-извлечение создало все {original.Directories.Count} папок, включая пустые (не создано {missingDirs.Count}: {Join(missingDirs)})");

        // Вариант «полный путь в DestName при DestPath = NULL» — отдельная сессия и отдельная папка,
        // чтобы основной ANSI-прогон не получил из-за него лишний вызов tProcessDataProc.
        (bool VariantOk, string VariantSource) variant = RunAnsiFullPathVariant(plugin, options, variantFile);
        Check(variant.VariantOk && File.Exists(variantFile), $"ANSI-копирование полным путём в DestName при DestPath = NULL работает: {Quote(variantFile)}");

        if (variant.VariantOk && variant.VariantSource.Length > 0 && original.Files.TryGetValue(variant.VariantSource, out (long VariantSize, string VariantSha256) sourceVariant))
        {
            Check(Sha256Of(variantFile) == sourceVariant.VariantSha256, $"полный путь в DestName: {Quote(variant.VariantSource)} извлечён в {Quote(variantFile)} с тем же SHA-256");
        }
        else
        {
            Skipped("нет исходного .txt-файла — сравнение варианта «полный путь в DestName» пропущено");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// ANSI-копирование, когда DestPath = NULL, а в DestName лежит полный путь до файла.
    /// Отдельная сессия без колбэка: основной прогон извлечения остаётся «чистым».
    /// </summary>
    private static (bool Ok, string Source) RunAnsiFullPathVariant(Plugin plugin, Options options, string variantFile)
    {
        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(options.Container);
        byte[] ansiFull = AnsiCodePage.ToAnsiZ(variantFile);

        fixed (byte* archiveName = ansiArchive)
        fixed (byte* fullPath = ansiFull)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
                return (false, string.Empty);

            string source = string.Empty;
            bool ok = false;

            while (true)
            {
                tHeaderDataEx header = default;
                header.CmtBuf = null;
                header.CmtBufSize = 0;

                int code = plugin.ReadHeaderEx(handle, &header);
                if (code != 0)
                    break;

                byte* raw = (byte*)&header;
                bool isDirectory = (ReadInt32(raw + WcxAnsi.OffExFileAttr) & Wcx.FA_DIRECTORY) != 0;
                string name = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes);

                if (!isDirectory && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    source = name;
                    ok = plugin.ProcessFile(handle, Wcx.PK_EXTRACT, null, fullPath) == 0;
                    break;
                }

                plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null);
            }

            Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после варианта с полным путём == 0");
            return (ok, source);
        }
    }

    private static void RunAnsiVerify(Plugin plugin, Options options, List<ArcEntry> unicodeListing)
    {
        Section("проход 11: ANSI PK_TEST (проверка «Проверить» в Total Commander)");

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(options.Container);
        byte[] comment = new byte[4096];
        int files = 0;
        int tested = 0;
        int failures = 0;
        string firstFailure = string.Empty;
        bool endedWithEndArchive = false;

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
                Fail($"OpenArchive(PK_OM_EXTRACT) для PK_TEST вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            while (true)
            {
                tHeaderDataEx header = default;
                int code = plugin.ReadHeaderEx(handle, &header);
                if (code == Wcx.E_END_ARCHIVE)
                {
                    endedWithEndArchive = true;
                    break;
                }

                if (code != 0)
                {
                    Fail($"ANSI ReadHeaderEx в PK_TEST вернул {Wcx.ErrorName(code)} после {tested} проверок");
                    break;
                }

                byte* raw = (byte*)&header;
                bool isDirectory = (ReadInt32(raw + WcxAnsi.OffExFileAttr) & Wcx.FA_DIRECTORY) != 0;
                string name = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes);

                int result = plugin.ProcessFile(handle, Wcx.PK_TEST, null, null);
                if (!isDirectory)
                    files++;

                if (result != 0)
                {
                    failures++;
                    if (firstFailure.Length == 0)
                        firstFailure = $"{Quote(name)} → {Wcx.ErrorName(result)}";
                }
                else if (!isDirectory)
                    tested++;
            }

            Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после PK_TEST == 0");
        }

        Check(endedWithEndArchive && failures == 0, $"ANSI PK_TEST прошёл все {files} файлов контейнера без ошибок (ошибок {failures}: {firstFailure})");
        Check(tested == unicodeListing.Count(e => !e.IsDirectory), $"ANSI PK_TEST проверил ровно {tested} файлов (в юникод-листинге их {unicodeListing.Count(e => !e.IsDirectory)})");
        Console.WriteLine();
    }

    private static void RunAnsiAbort(Plugin plugin, Options options)
    {
        Section("проход 12: остановка из ANSI-колбэка (tProcessDataProc возвращает 0 → E_EABORTED)");

        string target = Path.Combine(options.AnsiWorkDir, "ansi-abort");
        ResetFolder(target);

        AnsiProgress? progress = null;
        try
        {
            progress = new AnsiProgress(abort: true);
        }
        catch (Exception ex)
        {
            Skipped($"ANSI-колбэк построить не удалось ({ex.GetType().Name}: {ex.Message}) — проверка E_EABORTED в ANSI-ветке пропущена");
            Console.WriteLine();
            return;
        }

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(options.Container);
        byte[] ansiTarget = AnsiCodePage.ToAnsiZ(target);

        fixed (byte* archiveName = ansiArchive)
        fixed (byte* targetPath = ansiTarget)
        using (progress)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_EXTRACT;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive(PK_OM_EXTRACT) для ANSI-остановки вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            Check(plugin.SetProcessDataProc(handle, progress.Handle) == 0, "SetProcessDataProc (ANSI) перед остановкой принят (0)");

            int code = 0;
            string name = string.Empty;
            bool isDirectory = true;

            while (isDirectory)
            {
                tHeaderDataEx header = default;
                code = plugin.ReadHeaderEx(handle, &header);
                if (code != 0)
                    break;

                byte* raw = (byte*)&header;
                name = AnsiCodePage.FromAnsi(raw + WcxAnsi.OffExFileName, WcxAnsi.ExNameBytes);
                isDirectory = (ReadInt32(raw + WcxAnsi.OffExFileAttr) & Wcx.FA_DIRECTORY) != 0;

                if (isDirectory)
                    plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null);
            }

            int result = 0;
            if (name.Length > 0)
            {
                byte[] ansiName = AnsiCodePage.ToAnsiZ(name);
                fixed (byte* entryName = ansiName)
                    result = plugin.ProcessFile(handle, Wcx.PK_EXTRACT, targetPath, entryName);
            }

            Check(plugin.CloseArchive(handle) == 0, "ANSI CloseArchive после остановки == 0");
            Check(code == Wcx.E_END_ARCHIVE || name.Length > 0, $"в ANSI-ветке найден первый файловый вход для остановки ({Quote(name)})");
            Check(result == Wcx.E_EABORTED, $"ANSI tProcessDataProc с нулевым ответом привела к E_EABORTED, получено {Wcx.ErrorName(result)}");
            Check(progress.Calls > 0, $"плагин спросил разрешение через ANSI tProcessDataProc ({progress.Calls} раз, {progress.Bytes} байт)");
            Check(name.Length == 0 || !File.Exists(Path.Combine(target, name.TrimEnd('\\'))), $"прерванный ANSI-файл не создан ({Quote(name)})");
        }

        Console.WriteLine();
    }

    private static void RunAnsiBrokenInputs(Plugin plugin, Options options)
    {
        Section("проход 13: ANSI-ветка на битых входах, пустом имени и чужом handle");

        string notAContainer = Path.Combine(options.AnsiWorkDir, "не-контейнер-ansi.txt");
        File.WriteAllText(notAContainer, "это не контейнер Container_V3; ANSI-ветка обязана честно отказать.");
        string missing = Path.Combine(options.AnsiWorkDir, "нет-такого-файла-ansi.cc");

        Check(RefuseOpenAnsi(plugin, notAContainer, "ANSI-открытие обычного текстового файла"), "ANSI-отказ на текстовом файле корректный (handle = 0, OpenResult = понятный код)");
        Check(RefuseOpenAnsi(plugin, missing, "ANSI-открытие отсутствующего .cc"), "ANSI-отказ на отсутствующем файле корректный");
        Check(RefuseOpenAnsi(plugin, string.Empty, "ANSI-открытие с пустым именем"), "ANSI-отказ на пустом имени архива корректный");

        tHeaderDataEx ex = default;
        int exCode = plugin.ReadHeaderEx(0, &ex);
        Check(exCode == Wcx.E_BAD_ARCHIVE, $"ANSI ReadHeaderEx без открытой сессии (handle = 0) вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(exCode)}");

        tHeaderData plain = default;
        int plainCode = plugin.ReadHeader(0, &plain);
        Check(plainCode == Wcx.E_BAD_ARCHIVE, $"ANSI ReadHeader без открытой сессии (handle = 0) вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(plainCode)}");

        int procCode = plugin.SetProcessDataProc(0, 0);
        Check(procCode == Wcx.E_BAD_ARCHIVE, $"ANSI SetProcessDataProc на чужом handle вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(procCode)}");

        int changeCode = plugin.SetChangeVolProc(0, 0);
        Check(changeCode == 0, $"ANSI SetChangeVolProc на чужом handle безвреден, получено {Wcx.ErrorName(changeCode)}");

        Check(plugin.CloseArchive(0) == 0, "CloseArchive(0) — общий для обеих веток — по-прежнему 0");

        byte[] ansiArchive = AnsiCodePage.ToAnsiZ(options.Container);
        fixed (byte* archiveName = ansiArchive)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;

            nint handle = plugin.OpenArchive(&data);
            if (handle == 0)
            {
                Fail($"OpenArchive для ANSI-гигиены вернул 0, OpenResult = {Wcx.ErrorName(data.OpenResult)}");
                return;
            }

            int withoutHeader = plugin.ProcessFile(handle, Wcx.PK_EXTRACT, null, null);
            Check(withoutHeader == Wcx.E_BAD_ARCHIVE, $"ANSI ProcessFile(PK_EXTRACT) без предварительного ReadHeaderEx вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(withoutHeader)}");

            tHeaderDataEx header = default;
            Check(plugin.ReadHeaderEx(handle, &header) == 0, "ANSI ReadHeaderEx на свежем handle вернул первую запись (0)");

            int badOperation = plugin.ProcessFile(handle, 99, null, null);
            Check(badOperation == Wcx.E_NOT_SUPPORTED, $"ANSI ProcessFile с неизвестной операцией (99) при активной записи вернула E_NOT_SUPPORTED, получено {Wcx.ErrorName(badOperation)}");

            Check(plugin.ProcessFile(handle, Wcx.PK_SKIP, null, null) == 0, "ANSI ProcessFile(PK_SKIP) первой записи вернул 0");

            Check(plugin.CloseArchive(handle) == 0, "первый ANSI CloseArchive == 0");
            Check(plugin.CloseArchive(handle) == 0, "повторный ANSI CloseArchive того же handle == 0");

            int afterClose = plugin.ReadHeaderEx(handle, &header);
            Check(afterClose == Wcx.E_BAD_ARCHIVE, $"ANSI ReadHeaderEx после CloseArchive вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(afterClose)}");

            int headerAfterClose = plugin.ReadHeader(handle, &plain);
            Check(headerAfterClose == Wcx.E_BAD_ARCHIVE, $"ANSI ReadHeader после CloseArchive вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(headerAfterClose)}");

            int processAfterClose = plugin.ProcessFile(handle, Wcx.PK_TEST, null, null);
            Check(processAfterClose == Wcx.E_BAD_ARCHIVE, $"ANSI ProcessFile(PK_TEST) после CloseArchive вернул E_BAD_ARCHIVE, получено {Wcx.ErrorName(processAfterClose)}");
        }

        Console.WriteLine();
    }

    /// <summary>Открывает ANSI-OpenArchive и проверяет честный отказ; возвращает true, если отказ корректный.</summary>
    private static bool RefuseOpenAnsi(Plugin plugin, string path, string what)
    {
        byte[] ansiName = AnsiCodePage.ToAnsiZ(path);

        fixed (byte* archiveName = ansiName)
        {
            tOpenArchiveData data = default;
            data.ArcName = archiveName;
            data.OpenMode = Wcx.PK_OM_LIST;
            data.OpenResult = 0;

            nint handle = plugin.OpenArchive(&data);
            bool clean = handle == 0 && data.OpenResult != 0;

            Console.WriteLine($"  {what}: handle = {(handle == 0 ? "0" : "выдан")}, OpenResult = {Wcx.ErrorName(data.OpenResult)} {(clean ? "OK" : "ПРОВАЛ")}");

            if (handle != 0)
                plugin.CloseArchive(handle);

            return clean;
        }
    }

    // ─────────────────────────── мелочи ───────────────────────────

    private static int ReadInt32(byte* p) => unchecked((int)ReadUInt32(p));

    private static uint ReadUInt32(byte* p) => p[0] | ((uint)p[1] << 8) | ((uint)p[2] << 16) | ((uint)p[3] << 24);

    private static long Combine(uint high, uint low) => ((long)high << 32) | low;

    private static void ResetFolder(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);

        Directory.CreateDirectory(path);
    }

    private static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Заполняет буфер 0xAA — «видимой грязью», которую плагин обязан затереть своими полями.</summary>
    private static void FillSentinel(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = 0xAA;
    }

    private static bool AllSentinel(byte[] buffer, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (buffer[i] != 0xAA)
                return false;

        return true;
    }

    private static unsafe bool AllSentinel(byte* buffer, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (buffer[i] != 0xAA)
                return false;

        return true;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i])
                return false;

        return true;
    }

    private static string Hex32(uint value) => "0x" + value.ToString("X8");

    /// <summary>Честная отметка «проверку выполнить нельзя» — она не считается отказом.</summary>
    private static void Skipped(string what) => Console.WriteLine("  ПРОПУСК " + what);

    private static bool IsZero(byte* p, int count)
    {
        for (int i = 0; i < count; i++)
            if (p[i] != 0)
                return false;

        return true;
    }

    private static string ReadUtf16(byte* p, int maxChars)
    {
        char* chars = (char*)p;
        int length = 0;
        while (length < maxChars && chars[length] != '\0')
            length++;

        return new string(chars, 0, length);
    }

    private static string Quote(string value) => "[" + value + "]";

    /// <summary>Плагин обещает, что его можно наполнять (иначе TC не станет звать PackFiles).</summary>
    private static bool HasPackCaps(int caps) => (caps & (Wcx.PK_CAPS_NEW | Wcx.PK_CAPS_MODIFY)) != 0;

    private static string Join(IEnumerable<string> values)
    {
        List<string> list = values.Take(6).ToList();
        if (list.Count == 0)
            return "нет";

        int total = values.Count();
        return string.Join(", ", list) + (total > list.Count ? $" … всего {total}" : string.Empty);
    }

    private static void Section(string title)
    {
        _currentSection = title;
        Console.WriteLine("=== " + title + " ===");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        Records.Add(new CheckRecord(_currentSection, what, ok));
        Console.WriteLine($"  {(ok ? "OK   " : "ПРОВАЛ")} {what}");
        if (!ok)
            Failures.Add(what);
    }

    private static void Fail(string what)
    {
        _checks++;
        Records.Add(new CheckRecord(_currentSection, what, false));
        Console.WriteLine($"  ПРОВАЛ {what}");
        Failures.Add(what);
    }

    private static int Report()
    {
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine(Failures.Count == 0
            ? $"ХАРНЕСС ПРОЙДЕН: { _checks} проверок, отказов нет"
            : $"ХАРНЕСС ПРОВАЛЕН: {Failures.Count} из {_checks} проверок");

        foreach (string failure in Failures)
            Console.WriteLine("  ! " + failure);

        WriteResults();
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Тав-разделённый итог для тестов: "section\tOK|FAIL\twhat". Имена секций и тексты проверок
    /// не содержат табуляции, поэтому файл разбирается без экранирования.
    /// </summary>
    private static void WriteResults()
    {
        if (ResultsPath is null)
            return;

        string path = Path.GetFullPath(ResultsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = File.CreateText(path);
        writer.WriteLine("section\tresult\twhat");
        foreach (CheckRecord record in Records)
            writer.WriteLine($"{record.Section}\t{(record.Ok ? "OK" : "FAIL")}\t{record.What}");
    }

    /// <summary>Делегат tProcessDataProcW: TC зовёт его, плагин сообщает прогресс и слышит «стоп».</summary>
    private sealed class Progress : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int ProcessDataProcDelegate([MarshalAs(UnmanagedType.LPWStr)] string? name, int size);

        private readonly ProcessDataProcDelegate _callback;
        private readonly bool _abort;

        public Progress(bool abort = false)
        {
            _abort = abort;
            _callback = OnData;
            Handle = Marshal.GetFunctionPointerForDelegate(_callback);
        }

        public IntPtr Handle { get; }
        public int Calls { get; private set; }
        public long Bytes { get; private set; }
        public List<string> Names { get; } = new();
        public List<int> Sizes { get; } = new();

        private int OnData(string? name, int size)
        {
            Calls++;
            Bytes += size;
            Names.Add(name ?? string.Empty);
            Sizes.Add(size);
            return _abort ? 0 : 1;
        }

        public void Dispose() => GC.KeepAlive(_callback);
    }

    /// <summary>
    /// Делегат tProcessDataProc «первого издания»: имя передаётся как char* в кодовой странице
    /// системы, размер — int. Указатель действителен только во время вызова, поэтому байты копируются.
    /// </summary>
    private sealed unsafe class AnsiProgress : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProcessDataProcDelegate(byte* name, int size);

        private readonly ProcessDataProcDelegate _callback;
        private readonly bool _abort;

        public AnsiProgress(bool abort = false)
        {
            _abort = abort;
            _callback = OnData;
            Handle = Marshal.GetFunctionPointerForDelegate(_callback);
        }

        public IntPtr Handle { get; }
        public int Calls { get; private set; }
        public long Bytes { get; private set; }
        public List<string> Names { get; } = new();
        public List<byte[]> RawNames { get; } = new();
        public List<long> Sizes { get; } = new();

        private int OnData(byte* name, int size)
        {
            Calls++;
            Bytes += size;
            RawNames.Add(AnsiCodePage.SnapshotBytes(name, 4096));
            Names.Add(AnsiCodePage.FromAnsi(name, 4096));
            Sizes.Add(size);
            return _abort ? 0 : 1;
        }

        public void Dispose() => GC.KeepAlive(_callback);
    }
}

internal sealed class Snapshot
{
    public Dictionary<string, (long Size, string Sha256)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Directories { get; } = new();

    public static Snapshot Collect(string root)
    {
        Snapshot snapshot = new();

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot.Directories.Add(Path.GetRelativePath(root, directory));

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            long size = new FileInfo(file).Length;
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            snapshot.Files[Path.GetRelativePath(root, file)] = (size, hash);
        }

        return snapshot;
    }
}

internal sealed class Options
{
    public string Plugin { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string WorkDir { get; set; } = string.Empty;
    public string AnsiWorkDir { get; set; } = string.Empty;
    public string? ComparePlugin { get; set; }
    public string? CompareArchive { get; set; }
    public string? ResultsPath { get; set; }
    public bool LayoutOnly { get; set; }
    public bool SkipSettings { get; set; }

    public static Options? Parse(string[] args)
    {
        Options options = new();

        // Флаги без значения (--layout-only, --skip-settings) сдвигают индекс на 1, параметры
        // вида «--флаг значение» — на 2; иначе флаги съели бы следующий флаг как своё значение.
        for (int i = 0; i < args.Length;)
        {
            string flag = args[i];
            bool needsValue = flag is not ("--layout-only" or "--skip-settings");

            if (needsValue && i + 1 >= args.Length)
            {
                Console.WriteLine($"Не хватает значения после {flag}");
                return null;
            }

            switch (flag)
            {
                case "--plugin": options.Plugin = Path.GetFullPath(args[i + 1]); break;
                case "--container": options.Container = Path.GetFullPath(args[i + 1]); break;
                case "--source": options.Source = Path.GetFullPath(args[i + 1]); break;
                case "--workdir": options.WorkDir = Path.GetFullPath(args[i + 1]); break;
                case "--compare-plugin": options.ComparePlugin = Path.GetFullPath(args[i + 1]); break;
                case "--compare-archive": options.CompareArchive = Path.GetFullPath(args[i + 1]); break;
                case "--results": options.ResultsPath = Path.GetFullPath(args[i + 1]); break;
                case "--layout-only": options.LayoutOnly = true; break;
                case "--skip-settings": options.SkipSettings = true; break;
                default:
                    Console.WriteLine($"Неизвестный параметр {flag}");
                    return null;
            }

            i += needsValue ? 2 : 1;
        }

        bool layoutOnly = options.LayoutOnly;
        if (!layoutOnly && (options.Plugin.Length == 0 || options.Container.Length == 0 || options.Source.Length == 0 || options.WorkDir.Length == 0))
        {
            Console.WriteLine("Нужны --plugin, --container, --source и --workdir");
            return null;
        }

        if (!layoutOnly)
            Directory.CreateDirectory(options.WorkDir);

        // ANSI-проходы пишут в свою папку: если --workdir уже назван «…-ansi», он и есть ANSI-папка,
        // иначе рядом создаётся «<workdir>-ansi» — артефакты «первого издания» не попадают
        // в рабочую папку Unicode-проходов (.harness\work остаётся нетронутым).
        if (!layoutOnly)
        {
            options.AnsiWorkDir = options.WorkDir.EndsWith("-ansi", StringComparison.OrdinalIgnoreCase)
                ? options.WorkDir
                : options.WorkDir + "-ansi";
            Directory.CreateDirectory(options.AnsiWorkDir);
        }

        return options;
    }
}
