using Container_V3.FileSystem;
using Container_V3.Packing;

namespace Container_V3.Modification;

/// <summary>
/// Изменение готового контейнера: добавление, удаление, уплотнение.
///
/// Ничего не переписывается заново. Новые чанки дописываются в конец файла — за старым индексом,
/// новый индекс дописывается следом, и только последние 16 байт заголовка (OffsetToIndex,
/// IndexCrc32, HeaderCrc32) решают, какому индексу верить. До этой правки контейнер целен и
/// читается по-старому; обрыв в любой точке между началом работы и правкой заголовка оставляет
/// читаемый контейнер с прежним содержимым, а хвост из новых байтов становится безвредным мусором.
///
/// Поэтому стоимость добавления и удаления не зависит от размера контейнера: 100 МБ или 10 ГБ —
/// пишется только новое. Удаление вообще не читает данные: из индекса убираются записи, а чанки
/// помеченных файлов остаются в файле «мёртвыми». Их собирает Compact.
///
/// Остановка по прогрессу (Stop в панели Total Commander, отмена в приложении на телефоне)
/// дополнительно отрезает дописанный хвост: файл возвращается к прежней длине, чтобы прерванная
/// операция не оставляла лишние десятки мегабайт. При обрыве процесса отрезать некому — тогда
/// хвост остаётся безвредным мусором, и его соберёт Compact.
///
/// Цена такого порядка — рост файла: после удаления файл меньше не становится, пока не сделано
/// уплотнение. Об этом сообщает ContainerHealth, и это же видно в логе плагина.
/// </summary>
public sealed class ContainerModifier
{
    private readonly IHasher _hasher;
    private readonly ISourceScanner _scanner;
    private readonly PackingOptions _options;
    private readonly Func<int, ICompressor> _compressorFactory;

    /// <summary>
    /// compressorFactory получает уровень сжатия: для существующего контейнера уровень берётся
    /// из его заголовка, иначе новые чанки окажутся сжаты не так, как обещает заголовок.
    /// </summary>
    public ContainerModifier(IHasher hasher, ISourceScanner scanner, PackingOptions options, Func<int, ICompressor> compressorFactory)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(compressorFactory);
        options.Validate();

        _hasher = hasher;
        _scanner = scanner;
        _options = options;
        _compressorFactory = compressorFactory;
    }

    /// <summary>
    /// Добавить источники «как есть»: файл под своим именем, папка — со своим содержимым
    /// (имена внутри неё складываются из пути папки). Режим командной строки.
    /// </summary>
    public ModificationReport AppendPaths(string containerPath, IReadOnlyList<string> sourcePaths, IPackingProgress? progress = null)
        => Append(containerPath, ContainerSources.FromPlainPaths(sourcePaths), progress);

    /// <summary>
    /// Добавить источники с явными именами внутри контейнера. Так работает WCX-плагин:
    /// имена приходят от панели (SubPath + AddList), а не выводятся из путей на диске.
    /// progress — необязательный: по нему вызывают, когда файл записан целиком (полоска прогресса).
    /// </summary>
    public ModificationReport Append(string containerPath, IReadOnlyList<ContainerSource> sources, IPackingProgress? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        if (sources.Count == 0)
            throw new ContainerFormatException("Список источников пуст.");

        List<SourceEntry> additions = ContainerSources.Expand(sources, _scanner);
        if (additions.Count == 0)
            throw new ContainerFormatException("Добавлять нечего: в источниках нет ни файлов, ни пустых папок.");

        // Как при pack: источники проверяются до записи, а не после распаковки.
        SourceManifest.Verify(additions);

        bool fresh = !File.Exists(containerPath) || new FileInfo(containerPath).Length == 0;
        ContainerSnapshot snapshot = fresh ? ContainerSnapshot.Empty(_options, _hasher) : ReadSnapshot(containerPath);

        MergePlan merge = MergeInto(snapshot.Index, additions);

        // Копия — до открытия файла на запись: File.Copy открывает источник с FileShare.Read,
        // а такой доступ несовместим с уже открытым на запись хэндлом.
        string? backup = fresh ? null : ContainerSafety.EnsureBackup(containerPath);

        try
        {
            return AppendCore(containerPath, snapshot, merge, backup, fresh, progress);
        }
        catch
        {
            // Свежий контейнер создавали мы и до индекса дело не дошло: на диске остался заголовок
            // с нулевым OffsetToIndex, то есть файл, который нельзя прочитать. Его и убираем,
            // чтобы после неудачи (или Stop в панели) не оставалась пустая видимость контейнера.
            if (fresh)
                TryRemoveUnfinished(containerPath);
            throw;
        }
    }

    private ModificationReport AppendCore(
        string containerPath,
        ContainerSnapshot snapshot,
        MergePlan merge,
        string? backup,
        bool fresh,
        IPackingProgress? progress)
    {
        using var fs = OpenForUpdate(containerPath, fresh);
        if (!fresh)
            VerifyUnchanged(snapshot, fs);

        var builder = new IndexBuilder();

        // Старые записи, которых добавление не коснулось, переносятся как есть. Старые чанки —
        // все до единого, включая мёртвые: номер чанка обязан совпадать с его позицией в списке.
        foreach (FileEntry file in merge.Kept)
            builder.AddFile(file);
        foreach (ChunkInfo chunk in snapshot.Index.Chunks)
            builder.AddChunk(chunk);

        int firstNewChunk = snapshot.Index.Chunks.Count;
        var plan = ChunkPlanner.Create(merge.Additions, snapshot.ChunkSize);

        using ICompressor compressor = _compressorFactory(snapshot.CompressionLevel);
        using var writer = new ContainerWriter(fs);

        ContainerIndex index;
        long offsetToIndex;

        try
        {
            if (fresh)
                writer.WriteHeader(snapshot.ChunkSize, _hasher.Length, snapshot.CompressionLevel);
            else
                writer.Seek(fs.Length); // стартуем за старым индексом: он к этому моменту уже не участвует в чтении

            ChunkWriter.Write(
                ChunkOrder.GroupByChunk(plan),
                snapshot.ChunkSize,
                writer,
                builder,
                compressor,
                () => _compressorFactory(snapshot.CompressionLevel),
                _hasher,
                firstNewChunk,
                progress);

            // Файл нулевого размера не даёт ни одного фрагмента, поэтому ChunkWriter его не встречал.
            // Отмечаем отдельно: на полоску прогресса нуль байт не влияет, но список файлов полный.
            if (progress is not null)
                ReportFragmentless(progress, merge.Additions);

            AddNewFiles(plan, merge.Additions, builder, firstNewChunk);

            index = builder.Build();
            offsetToIndex = IndexWriter.Write(writer, index, compressor, snapshot.ChunkSize, _hasher.Length, snapshot.CompressionLevel);

            fs.Flush();
        }
        catch
        {
            // До PatchHeader — единственной точки коммита — дело не дошло. Дыру в заголовке закрывать
            // нечем, иначе Dispose выдал бы «Контейнер не завершён» вместо настоящей причины.
            writer.Abandon();

            // Хвост новых чантов формально безвреден: старый индекс цел, контейнер читается по-старому.
            // Но оставленный на десятках мегабайт он выглядит как испорченная операция: человек нажал
            // Stop, а файл вырос. Всё до snapshot.FileLength при добавлении не трогалось (пишем строго
            // в конец), поэтому отрезаем дописанное и возвращаем файлу прежнюю длину. Если отрезать не
            // удалось — оставляем хвост: это мусор, который соберёт Compact, и перекрывать им настоящую
            // причину ошибки нельзя.
            if (!fresh)
                TryRestoreLength(fs, snapshot.FileLength);

            throw;
        }

        return new ModificationReport(
            containerPath,
            merge.Additions.Count,
            merge.Replaced,
            RemovedEntries: 0,
            PreservedEmptyFolders: 0,
            plan.ChunkCount,
            fs.Length - snapshot.FileLength,
            fs.Length,
            ContainerHealth.Measure(fs.Length, offsetToIndex, _hasher.Length, index),
            backup);
    }

    /// <summary>Отчёт по файлам без фрагментов: они существуют в контейнере, но чанков не создали.</summary>
    private static void ReportFragmentless(IPackingProgress progress, List<SourceEntry> additions)
    {
        foreach (SourceEntry entry in additions)
        {
            if (entry.IsDirectory || entry.Size > 0)
                continue;

            if (!progress.FilePacked(entry.FullPath, 0))
                throw new PackingAbortedException($"Упаковка прервана на файле '{entry.FullPath}'.");
        }
    }

    /// <summary>
    /// Вернуть файлу длину, которая была до операции: отрезать чанты, дописанные до остановки.
    /// Вызывается только до точки коммита, поэтому прежние байты не менялись — срез безопасен.
    /// </summary>
    private static void TryRestoreLength(FileStream fs, long originalLength)
    {
        try
        {
            if (fs.Length > originalLength)
            {
                fs.Flush(flushToDisk: true);
                fs.SetLength(originalLength);
                fs.Flush(flushToDisk: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Убрать файл, который начали создавать и не дописали. Ошибку при этом не раздуваем.</summary>
    private static void TryRemoveUnfinished(string containerPath)
    {
        try
        {
            if (File.Exists(containerPath))
                File.Delete(containerPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Удалить записи по именам. Имя папки удаляет её целиком вместе с содержимым — как в обычной папке.
    /// Данные не читаются и не переписываются: меняется только индекс, поэтому удаление стоит
    /// одинаково для контейнера на 5 МБ и на 10 ГБ.
    /// </summary>
    public ModificationReport Remove(string containerPath, IReadOnlyList<string> relativePaths) =>
        Remove(containerPath, RemovalPattern.ParseAll(relativePaths));

    /// <summary>
    /// Удалить записи по именам и маскам. Помимо имён работает форма «папка\*.*», которой
    /// Total Commander передаёт удаление папки: см. RemovalPattern.
    /// </summary>
    public ModificationReport Remove(string containerPath, IReadOnlyList<RemovalPattern> patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        if (!File.Exists(containerPath))
            throw new FileNotFoundException($"Контейнер не найден: {containerPath}", containerPath);

        var wanted = new List<RemovalPattern>(patterns.Count);
        foreach (RemovalPattern pattern in patterns)
            wanted.Add(pattern);

        if (wanted.Count == 0)
            throw new ContainerFormatException("Список удаляемых имён пуст.");

        ContainerSnapshot snapshot = ReadSnapshot(containerPath);

        var kept = new List<FileEntry>(snapshot.Index.Files.Count);
        var removedPaths = new List<string>();
        int removed = 0;

        foreach (FileEntry entry in snapshot.Index.Files)
        {
            string path = ContainerPath.Normalize(entry.Path);

            if (MatchesAny(path, wanted))
            {
                removed++;
                removedPaths.Add(path);
                continue;
            }

            kept.Add(entry with { Path = path });
        }

        if (removed == 0)
            throw new NothingToRemoveException($"В контейнере нет ничего из перечисленного: {string.Join("; ", wanted)}");

        // Папка, из которой удалён последний файл, не должна исчезать: в обычной файловой системе
        // каталог остаётся пустым, а не испаряется. В контейнере каталоги существуют только как
        // часть имён файлов, поэтому освободившийся каталог надо записать явно — иначе после
        // удаления он пропал бы из списка и исчезал бы при распаковке.
        int preserved = KeepEmptiedFolders(kept, removedPaths, wanted);

        string? backup = ContainerSafety.EnsureBackup(containerPath);

        using var fs = OpenForUpdate(containerPath, fresh: false);
        VerifyUnchanged(snapshot, fs);

        var builder = new IndexBuilder();
        foreach (FileEntry entry in kept)
            builder.AddFile(entry);
        foreach (ChunkInfo chunk in snapshot.Index.Chunks)
            builder.AddChunk(chunk); // мёртвые чанки остаются: их номера должны совпадать с позициями

        ContainerIndex index = builder.Build();

        using ICompressor compressor = _compressorFactory(snapshot.CompressionLevel);
        using var writer = PrepareAppend(fs);
        long offsetToIndex = IndexWriter.Write(writer, index, compressor, snapshot.ChunkSize, _hasher.Length, snapshot.CompressionLevel);

        fs.Flush();

        return new ModificationReport(
            containerPath,
            AddedEntries: 0,
            ReplacedEntries: 0,
            RemovedEntries: removed,
            PreservedEmptyFolders: preserved,
            AddedChunks: 0,
            fs.Length - snapshot.FileLength,
            fs.Length,
            ContainerHealth.Measure(fs.Length, offsetToIndex, _hasher.Length, index),
            backup);
    }

    /// <summary>
    /// Собрать мусор: переписать живые чанки дословно (без переупаковки и без пережатия) в новый
    /// файл и заменить им старый. Номера чанков уплотняются плотно, ссылки файлов переставляются
    /// тем же обходом.
    ///
    /// Это единственная операция, которая читает контейнер целиком. Поэтому она отдельная команда,
    /// а не побочный эффект добавления или удаления.
    /// </summary>
    public CompactReport Compact(string containerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        if (!File.Exists(containerPath))
            throw new FileNotFoundException($"Контейнер не найден: {containerPath}", containerPath);

        ContainerSnapshot snapshot = ReadSnapshot(containerPath);
        ContainerHealth before = ContainerHealth.Measure(snapshot.FileLength, snapshot.Header.OffsetToIndex, _hasher.Length, snapshot.Index);

        if (before.ReclaimableBytes == 0)
            return new CompactReport(containerPath, snapshot.FileLength, snapshot.FileLength, 0, before.LiveChunkCount, Applied: false, BackupPath: null);

        string tempPath = TempPath(containerPath);
        TryDelete(tempPath); // остаток прерванного уплотнения: новый файл никому не нужен, старый на месте

        string? backup = ContainerSafety.EnsureBackup(containerPath);

        // «Старый номер чанка → новый номер» нужен и чтобы переставить ссылки файлов,
        // и чтобы потом проверить полученный файл.
        var numberMap = new Dictionary<int, int>();
        var newChunks = new List<ChunkInfo>();
        int liveChunks = 0;

        try
        {
            using var source = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.RandomAccess);
            using var reader = new ContainerReader(source);

            using var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            using var writer = new ContainerWriter(target);

            try
            {
                writer.WriteHeader(snapshot.ChunkSize, _hasher.Length, snapshot.CompressionLevel);

                liveChunks = CopyLiveChunks(snapshot, reader, writer, newChunks, numberMap);

                var builder = new IndexBuilder();
                foreach (FileEntry file in RebuildFiles(snapshot, numberMap))
                    builder.AddFile(file);
                foreach (ChunkInfo chunk in newChunks)
                    builder.AddChunk(chunk);

                using ICompressor compressor = _compressorFactory(snapshot.CompressionLevel);
                IndexWriter.Write(writer, builder.Build(), compressor, snapshot.ChunkSize, _hasher.Length, snapshot.CompressionLevel);

                target.Flush();
            }
            catch
            {
                // Незавершённое уплотнение снимается вместе с временным файлом ниже; здесь важно,
                // чтобы Dispose не подменил настоящую причину своим «Контейнер не завершён».
                writer.Abandon();
                throw;
            }
        }
        catch (Exception ex)
        {
            // Недостроенный временный файл не должен оставаться на диске после неудачи.
            TryDelete(tempPath);
            if (ex is ContainerFormatException or ChunkIntegrityException)
                throw;
            throw new ContainerFormatException($"Уплотнение не завершено: {ex.Message}", ex);
        }

        VerifyCompacted(tempPath, snapshot, numberMap, liveChunks);
        ReplaceContainer(containerPath, tempPath);

        long sizeAfter = new FileInfo(containerPath).Length;

        return new CompactReport(
            containerPath,
            snapshot.FileLength,
            sizeAfter,
            DroppedChunks: before.DeadChunkCount,
            LiveChunks: liveChunks,
            Applied: true,
            backup);
    }

    // --- служебное -------------------------------------------------------

    /// <summary>
    /// Перенос живых чанков дословно: прочитал сжатые байты и их хэш — записал теми же байтами.
    /// Возвращает количество перенесённых чанков; заодно заполняет список чанков нового файла
    /// и сопоставление «старый номер → новый номер».
    /// </summary>
    private int CopyLiveChunks(ContainerSnapshot snapshot, ContainerReader reader, ContainerWriter writer, List<ChunkInfo> newChunks, Dictionary<int, int> numberMap)
    {
        var referenced = new HashSet<int>();
        foreach (FileEntry file in snapshot.Index.Files)
            foreach (ChunkFragmentRef fragment in file.Fragments)
                referenced.Add(fragment.ChunkNumber);

        int newNumber = 0;
        Span<byte> computedHash = stackalloc byte[_hasher.Length];

        foreach (ChunkInfo chunk in snapshot.Index.Chunks)
        {
            if (!referenced.Contains(chunk.ChunkNumber))
                continue;

            long compressedLength = chunk.HashPosition - chunk.OffsetInContainer;

            using PooledBuffer data = new((int)compressedLength);
            reader.ReadInto(chunk.OffsetInContainer, data.Span);

            using PooledBuffer storedHash = new(_hasher.Length);
            reader.ReadInto(chunk.HashPosition, storedHash.Span);

            // Хэш проверяется до того, как байты станут частью нового файла: уплотнение не должно
            // молча переносить повреждённый чанк в «аккуратный» контейнер.
            _hasher.Hash(data.Span, computedHash);
            if (!computedHash.SequenceEqual(storedHash.Span))
                throw new ChunkIntegrityException(chunk.ChunkNumber);

            long offset = writer.Position;
            writer.Write(data.Span);
            long hashPosition = writer.Position;
            writer.Write(storedHash.Span);

            newChunks.Add(new ChunkInfo(newNumber, offset, hashPosition, chunk.UncompressedLength));
            numberMap[chunk.ChunkNumber] = newNumber;
            newNumber++;
        }

        return newNumber;
    }

    /// <summary>
    /// Перестроить записи файлов под новые номера чанков. Порядок файлов сохраняется —
    /// панель показывает список именно в том виде, в каком он записан.
    /// </summary>
    private static List<FileEntry> RebuildFiles(ContainerSnapshot snapshot, Dictionary<int, int> numberMap)
    {
        var files = new List<FileEntry>(snapshot.Index.Files.Count);

        foreach (FileEntry file in snapshot.Index.Files)
        {
            var refs = new List<ChunkFragmentRef>(file.Fragments.Count);

            foreach (ChunkFragmentRef fragment in file.Fragments)
            {
                if (!numberMap.TryGetValue(fragment.ChunkNumber, out int newNumber))
                    throw new ContainerFormatException($"Файл '{file.Path}' ссылается на отсутствующий чанк #{fragment.ChunkNumber}.");

                refs.Add(new ChunkFragmentRef(newNumber, fragment.OffsetInChunk, fragment.BlockSize));
            }

            files.Add(file with { Path = ContainerPath.Normalize(file.Path), Fragments = refs });
        }

        return files;
    }

    /// <summary>Поставить писателя в конец файла — за существующими данными и старым индексом.</summary>
    private static ContainerWriter PrepareAppend(FileStream fs)
    {
        var writer = new ContainerWriter(fs);
        writer.Seek(fs.Length);
        return writer;
    }

    private static void AddNewFiles(PackingPlan plan, List<SourceEntry> additions, IndexBuilder builder, int firstNewChunk)
    {
        foreach (SourceEntry entry in additions)
        {
            if (entry.IsDirectory || entry.Size == 0)
            {
                // Пустая папка и файл нулевого размера существуют только в индексе — чанков у них нет.
                builder.AddFile(new FileEntry(entry.RelativePath, 0, entry.IsDirectory, false, []));
                continue;
            }

            List<ChunkFragment> fragments = plan.ByFile[entry.FullPath];
            var refs = new List<ChunkFragmentRef>(fragments.Count);

            foreach (ChunkFragment fragment in fragments)
                refs.Add(new ChunkFragmentRef(firstNewChunk + fragment.ChunkIndex, fragment.OffsetInChunk, fragment.DataLength));

            builder.AddFile(new FileEntry(entry.RelativePath, entry.Size, false, fragments.Count > 1, refs));
        }
    }

    /// <summary>
    /// Совмещение существующего индекса с новыми записями.
    /// Одинаковое имя — замена: запись в контейнере становится тем, что лежит на диске сейчас
    /// (так же, как копирование в обычную папку перезаписывает файл).
    /// </summary>
    private static MergePlan MergeInto(ContainerIndex existing, List<SourceEntry> additions)
    {
        var normalizedExisting = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FileEntry entry in existing.Files)
        {
            string path = ContainerPath.Normalize(entry.Path);

            if (!normalizedExisting.TryAdd(path, entry with { Path = path }))
                throw new ContainerFormatException($"В индексе две записи с одним именем: '{path}'.");

            if (entry.IsEmptyFolder)
                existingFolders.Add(path);
            else
                existingFiles.Add(path);

            // «a\b\c.bin» означает, что каталоги «a» и «a\b» в контейнере уже есть.
            foreach (string ancestor in ContainerPath.Ancestors(path))
                existingFolders.Add(ancestor);
        }

        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SourceEntry entry in additions)
        {
            string path = entry.RelativePath;

            if (entry.IsDirectory)
            {
                if (existingFiles.Contains(path))
                    throw new ContainerFormatException($"Нельзя добавить папку '{path}': в контейнере уже есть файл с таким именем.");
            }
            else
            {
                if (existingFolders.Contains(path))
                    throw new ContainerFormatException($"Нельзя добавить файл '{path}': в контейнере уже есть файлы внутри папки с таким именем.");

                foreach (string ancestor in ContainerPath.Ancestors(path))
                {
                    if (existingFiles.Contains(ancestor))
                        throw new ContainerFormatException($"Нельзя добавить файл '{path}': '{ancestor}' — файл, а не папка.");
                    addedAncestors.Add(ancestor);
                }
            }

            addedPaths.Add(path);
        }

        var kept = new List<FileEntry>(existing.Files.Count);
        int replaced = 0;

        foreach (FileEntry entry in existing.Files)
        {
            string path = ContainerPath.Normalize(entry.Path);

            if (addedPaths.Contains(path))
            {
                replaced++;
                continue;
            }

            // Запись «папка» существует в индексе только для пустых папок; если внутри появился
            // файл, запись устарела — каталог и так виден из путей вложенных файлов.
            if (entry.IsEmptyFolder && addedAncestors.Contains(path))
                continue;

            kept.Add(normalizedExisting[path]);
        }

        return new MergePlan(kept, additions, replaced);
    }

    /// <summary>
    /// Записать в индекс пустые каталоги, которые образовались после удаления.
    ///
    /// Правило прежнее: отдельной записи «папка» в контейнере нет, папка — это часть имён её файлов.
    /// Значит каталог существует, пока под ним есть хотя бы одна запись. Удаление последней записи
    /// каталог уничтожало, и в панели он исчезал вместе с содержимым — как будто удалили саму папку.
    /// Здесь освободившийся каталог получает обычную запись пустой папки, ровно такую же, какую
    /// создаёт pack для пустой папки на диске.
    ///
    /// Каталоги внутри удалённой подпапки не возрождаются: её удалили целиком. Предок над удалённой
    /// записью — наоборот, остаётся, и прерываем обход на первом же предке, под которым что-то
    /// осталось: выше он уже виден через этот каталог.
    /// </summary>
    private static int KeepEmptiedFolders(List<FileEntry> kept, List<string> removedPaths, List<RemovalPattern> wanted)
    {
        // Каталоги, под которыми что-то осталось, и уже записанные пустые папки. Занятость
        // расчётная: «a\b\c.bin» занимает и «a\b», и «a».
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaves = new List<string>(kept.Count);

        foreach (FileEntry entry in kept)
        {
            occupied.Add(entry.Path);
            leaves.Add(entry.Path);

            foreach (string ancestor in ContainerPath.Ancestors(entry.Path))
                occupied.Add(ancestor);
        }

        int added = 0;

        foreach (string removedPath in removedPaths)
        {
            string[] ancestors = [.. ContainerPath.Ancestors(removedPath)];

            // Снизу вверх: запись нужна самому ближнему освободившемуся каталогу — его предки
            // увидны в списке через него самого.
            for (int i = ancestors.Length - 1; i >= 0; i--)
            {
                string folder = ancestors[i];

                // Под каталогом ещё что-то есть: он и так виден из имён этих записей. Все его
                // предки заняты тем же самым, поэтому дальше идти не нужно.
                if (occupied.Contains(folder) || HasOccupiedUnder(folder, leaves))
                    break;

                // Каталог лежит внутри того, что попросили удалить (сам удалён или внутри удалённой
                // папки): его удалили по имени, возрождать нельзя. Идём выше — предки над удалённой
                // папкой никуда не делись и должны остаться.
                if (MatchesAny(folder, wanted))
                    continue;

                occupied.Add(folder);
                leaves.Add(folder);
                kept.Add(new FileEntry(folder, 0, IsEmptyFolder: true, IsSplitted: false, []));
                added++;
            }
        }

        return added;
    }

    private static bool HasOccupiedUnder(string folder, List<string> leaves)
    {
        foreach (string leaf in leaves)
            if (ContainerPath.IsAncestor(folder, leaf))
                return true;

        return false;
    }

    private static bool MatchesAny(string path, List<RemovalPattern> wanted)
    {
        foreach (RemovalPattern pattern in wanted)
        {
            if (pattern.Matches(path))
                return true;
        }
        return false;
    }

    private ContainerSnapshot ReadSnapshot(string containerPath)
    {
        using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.RandomAccess);

        using var reader = new ContainerReader(stream);
        ContainerHeader header = reader.ReadHeader();

        if (header.HashLength != _hasher.Length)
            throw new ContainerFormatException(
                $"Длина хэша в контейнере {header.HashLength}, эта реализация считает {_hasher.Length}: " +
                "параметры существующего контейнера менять нельзя, переупакуйте источник.");

        if (header.ChunkSize < ContainerFormat.MinChunkSize || header.ChunkSize > ContainerFormat.MaxChunkSize)
            throw new ContainerFormatException($"Размер чанка в заголовке вне границ формата: {header.ChunkSize}.");

        int level = header.CompressionLevel > 0 ? header.CompressionLevel : _options.CompressionLevel;

        using ICompressor compressor = _compressorFactory(level);
        ContainerIndex index = reader.ReadIndex(header, compressor);

        ValidateChunks(index, header, stream.Length, compressor);

        return new ContainerSnapshot(header, index, stream.Length, header.ChunkSize, level);
    }

    /// <summary>
    /// Проверка индекса прежде, чем на него опрётся дописывание.
    /// Порядок чанков в списке обязан совпадать с их номерами — на этом держится вся распаковка
    /// (RestorePlanner берёт index.Chunks[fragment.ChunkNumber]), и это единственное место, где
    /// нарушение видно до того, как контейнер станет нечитаемым.
    /// </summary>
    private static void ValidateChunks(ContainerIndex index, ContainerHeader header, long fileLength, ICompressor compressor)
    {
        long previousEnd = ContainerHeader.Size;

        for (int i = 0; i < index.Chunks.Count; i++)
        {
            ChunkInfo chunk = index.Chunks[i];

            if (chunk.ChunkNumber != i)
                throw new ContainerFormatException(
                    $"Индекс не соответствует порядку чанков: на позиции {i} записан чанк #{chunk.ChunkNumber}. " +
                    "Чанки в индексе должны идти строго по номерам, пропущенных быть не может.");

            if (chunk.OffsetInContainer < ContainerHeader.Size || chunk.OffsetInContainer < previousEnd)
                throw new ContainerFormatException($"Чанк #{chunk.ChunkNumber} перекрывает предыдущие данные: смещение {chunk.OffsetInContainer}.");

            long regionEnd = chunk.HashPosition + header.HashLength;
            if (chunk.HashPosition <= chunk.OffsetInContainer || regionEnd > header.OffsetToIndex || regionEnd > fileLength)
                throw new ContainerFormatException(
                    $"Чанк #{chunk.ChunkNumber} выходит за пределы области данных: {chunk.OffsetInContainer}..{regionEnd} " +
                    $"при OffsetToIndex {header.OffsetToIndex} и размере {fileLength}.");

            if (chunk.UncompressedLength <= 0 || chunk.UncompressedLength > header.ChunkSize)
                throw new ContainerFormatException($"Длина чанка #{chunk.ChunkNumber} до сжатия невозможна: {chunk.UncompressedLength} при chunkSize {header.ChunkSize}.");

            // Верхняя граница сжатых байт следует из длины до сжатия: без неё испорченный индекс
            // заставил бы уплотнение читать «чанк» на весь файл.
            long region = chunk.HashPosition - chunk.OffsetInContainer;
            if (region > compressor.MaxCompressedLength(chunk.UncompressedLength))
                throw new ContainerFormatException(
                    $"Чанк #{chunk.ChunkNumber} заявлен в {region} байт сжатых данных — больше, чем возможно " +
                    $"для {chunk.UncompressedLength} байт до сжатия.");

            previousEnd = regionEnd;
        }
    }

    private static void VerifyUnchanged(ContainerSnapshot snapshot, FileStream fs)
    {
        // Читатель здесь не dispose-ится: поток принадлежит вызывающему коду, а не читателю.
        ContainerHeader current = new ContainerReader(fs).ReadHeader();

        if (fs.Length != snapshot.FileLength || current.OffsetToIndex != snapshot.Header.OffsetToIndex)
            throw new ContainerFormatException(
                "Контейнер изменился другой программой, пока готовилось изменение. Повторите операцию.");
    }

    private static FileStream OpenForUpdate(string containerPath, bool fresh) =>
        new(containerPath,
            fresh ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            1 << 20,
            FileOptions.RandomAccess);

    private static string TempPath(string containerPath)
    {
        string full = Path.GetFullPath(containerPath);
        string? directory = Path.GetDirectoryName(full);
        return Path.Combine(directory ?? ".", Path.GetFileName(full) + ".compact.tmp");
    }

    /// <summary>
    /// Проверить недостроенный файл так, как его проверит любой читатель: прочитать заголовок
    /// и индекс целиком и сравнить с тем, что должно было получиться.
    /// Замена старого контейнера новым без этой проверки означала бы, что ошибка уплотнения
    /// становится видна только когда пользователь попытается прочитать результат.
    /// </summary>
    private void VerifyCompacted(string tempPath, ContainerSnapshot snapshot, Dictionary<int, int> numberMap, int expectedChunks)
    {
        using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.RandomAccess);
        using var reader = new ContainerReader(stream);

        ContainerHeader header = reader.ReadHeader();
        using ICompressor compressor = _compressorFactory(snapshot.CompressionLevel);
        ContainerIndex index = reader.ReadIndex(header, compressor);

        ValidateChunks(index, header, stream.Length, compressor);

        if (index.Chunks.Count != expectedChunks)
            throw new ContainerFormatException($"Уплотнение сохранило {index.Chunks.Count} чанков вместо {expectedChunks}.");

        // Ожидаемое содержимое — те же записи с переставленными номерами чанков.
        var expected = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry file in RebuildFiles(snapshot, numberMap))
        {
            if (!expected.TryAdd(file.Path, file))
                throw new ContainerFormatException($"В исходном индексе две записи с одним именем: '{file.Path}'.");
        }

        if (index.Files.Count != expected.Count)
            throw new ContainerFormatException($"Уплотнение изменило количество записей: было {expected.Count}, стало {index.Files.Count}.");

        foreach (FileEntry file in index.Files)
        {
            string path = ContainerPath.Normalize(file.Path);

            if (!expected.TryGetValue(path, out FileEntry? want))
                throw new ContainerFormatException($"Уплотнение: в новом контейнере появилась лишняя запись '{path}'.");

            if (want.Size != file.Size || want.IsEmptyFolder != file.IsEmptyFolder || want.Fragments.Count != file.Fragments.Count)
                throw new ContainerFormatException($"Уплотнение исказило запись '{path}'.");

            for (int i = 0; i < want.Fragments.Count; i++)
            {
                ChunkFragmentRef a = want.Fragments[i];
                ChunkFragmentRef b = file.Fragments[i];

                if (a.ChunkNumber != b.ChunkNumber || a.OffsetInChunk != b.OffsetInChunk || a.BlockSize != b.BlockSize)
                    throw new ContainerFormatException($"Уплотнение исказило ссылки файла '{path}'.");
            }
        }
    }

    private static void ReplaceContainer(string containerPath, string tempPath)
    {
        try
        {
            File.Replace(tempPath, containerPath, destinationBackupFileName: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Чаще всего контейнер в этот момент открыт панелью или антивирусом.
            throw new ContainerFormatException(
                $"Не удалось заменить контейнер '{containerPath}': {ex.Message}. Новый файл сохранён отдельно: '{tempPath}'.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Невозможность удалить остаток не должна мешать уплотнению: CreateNew тогда сам скажет о проблеме.
        }
    }

    private sealed record MergePlan(List<FileEntry> Kept, List<SourceEntry> Additions, int Replaced);

    /// <summary>Заголовок, индекс и размер файла на тот момент, когда мы их прочитали.</summary>
    private sealed record ContainerSnapshot(ContainerHeader Header, ContainerIndex Index, long FileLength, int ChunkSize, int CompressionLevel)
    {
        public static ContainerSnapshot Empty(PackingOptions options, IHasher hasher) => new(
            new ContainerHeader(ContainerHeader.CurrentVersion, options.ChunkSize, hasher.Length, options.CompressionLevel, 0L),
            new ContainerIndex([], []),
            0L,
            options.ChunkSize,
            options.CompressionLevel);
    }
}
