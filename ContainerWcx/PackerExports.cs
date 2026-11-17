using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Container_V3;
using Container_V3.Compressors;
using Container_V3.Hashers;
using Container_V3.FileSystem;
using Container_V3.Modification;
using Container_V3.Packing;

namespace ContainerWcx;

/// <summary>
/// Упаковка и удаление из контейнера: PackFilesW/PackFiles и DeleteFilesW/DeleteFiles (WCX 2.21).
/// Именно это даёт в Total Commander «копировать в контейнер как в папку» и «удалить из контейнера».
///
/// Семантика вызова (docs/packfiles.htm, docs/deletefiles.htm):
///   PackFiles(PackedFile, SubPath, SrcPath, AddList, Flags) — PackedFile полный путь контейнера,
///   его создают или меняют; SrcPath + AddList задают файлы на диске; SubPath — префикс внутри
///   архива (NULL = имена из AddList используют как есть). AddList — строки, разделённые нулями,
///   и ещё один ноль в конце списка.
///   DeleteFiles(PackedFile, DeleteList) — имена ровно такие, какие плагин показал в списке.
///
/// Обе ветки (W и ANSI) экспортируются: 64-битный TC сначала ищет W-имена, потом ANSI
/// (docs/64_bit_support.htm). Исключения через границу не проходят — как и в остальных экспортах,
/// наружу идёт только код E_*.
/// </summary>
internal static class PackerExports
{
    [UnmanagedCallersOnly(EntryPoint = "PackFilesW")]
    public static unsafe int PackFilesW(char* packedFile, char* subPath, char* srcPath, char* addList, int flags)
    {
        string container = "?";
        try
        {
            container = NativeStrings.ReadZ(packedFile);
            int result = PackCore(container, NativeStrings.ReadZ(subPath), NativeStrings.ReadZ(srcPath),
                NativeStrings.ReadZList(addList), flags);
            PluginTrace.Write($"PackFilesW('{container}') flags={flags} → {(result == 0 ? "OK" : WcxErrors.Name(result))}");
            return result;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"PackFilesW('{container}'): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapPackError(ex);
        }
    }

    /// <summary>ANSI-ветка PackFiles: имена в кодовой странице системы.</summary>
    [UnmanagedCallersOnly(EntryPoint = "PackFiles")]
    public static unsafe int PackFiles(byte* packedFile, byte* subPath, byte* srcPath, byte* addList, int flags)
    {
        string container = "?";
        try
        {
            container = NativeStrings.ReadZAnsi(packedFile);
            int result = PackCore(container, NativeStrings.ReadZAnsi(subPath), NativeStrings.ReadZAnsi(srcPath),
                NativeStrings.ReadZListAnsi(addList), flags);
            PluginTrace.Write($"PackFiles('{container}') flags={flags} → {(result == 0 ? "OK" : WcxErrors.Name(result))}");
            return result;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"PackFiles('{container}'): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapPackError(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "DeleteFilesW")]
    public static unsafe int DeleteFilesW(char* packedFile, char* deleteList)
    {
        string container = "?";
        try
        {
            container = NativeStrings.ReadZ(packedFile);
            int result = DeleteCore(container, NativeStrings.ReadZList(deleteList));
            PluginTrace.Write($"DeleteFilesW('{container}') → {(result == 0 ? "OK" : WcxErrors.Name(result))}");
            return result;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"DeleteFilesW('{container}'): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapPackError(ex);
        }
    }

    /// <summary>ANSI-ветка DeleteFiles.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DeleteFiles")]
    public static unsafe int DeleteFiles(byte* packedFile, byte* deleteList)
    {
        string container = "?";
        try
        {
            container = NativeStrings.ReadZAnsi(packedFile);
            int result = DeleteCore(container, NativeStrings.ReadZListAnsi(deleteList));
            PluginTrace.Write($"DeleteFiles('{container}') → {(result == 0 ? "OK" : WcxErrors.Name(result))}");
            return result;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"DeleteFiles('{container}'): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapPackError(ex);
        }
    }

    /// <summary>
    /// PackSetDefaultParams приходит сразу после загрузки DLL (SDK 2.1+): версия интерфейса TC и
    /// путь к ini с общими настройками пакеров (у TC это pkplugin.ini рядом с программой).
    /// Из вызова берём и запоминаем именно этот путь: в свою секцию [ContainerCC] плагин пишет
    /// уровень сжатия и размер чанка, которые пользователь выбирает в диалоге «Настроить».
    /// При дописывании в готовый контейнер эти значения не участвуют — модификатор берёт чанк и
    /// уровень из его заголовка.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "PackSetDefaultParams")]
    public static unsafe void PackSetDefaultParams(PackDefaultParamStruct* dps)
    {
        try
        {
            if (dps == null)
                return;

            string iniPath = NativeStrings.ReadZAnsi(dps->DefaultIniName);

            PluginTrace.Write(
                $"PackSetDefaultParams: struct size {dps->size}, интерфейс TC {dps->PluginInterfaceVersionHi}.{dps->PluginInterfaceVersionLow}, " +
                $"ini {iniPath}");

            PackerSettings.RememberTcIni(iniPath);
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"PackSetDefaultParams: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// ConfigurePacker вызывает Total Commander по кнопке «Настроить» в окне «Упаковать файлы»
    /// (Alt+F5). Диалог делается дочерним к Parent, как требует SDK (docs/configurepacker.htm).
    /// Экспорт работает только вместе с флагом PK_CAPS_OPTIONS в GetPackerCaps: без него TC кнопку
    /// «Настроить» не показывает. Через границу наружу не уходит ничего: диалог сам ловит свои ошибки,
    /// здесь ещё одна страховка.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ConfigurePacker")]
    public static void ConfigurePacker(IntPtr parent, IntPtr dllInstance)
    {
        try
        {
            PluginTrace.Write(
                $"ConfigurePacker: родитель {(parent == IntPtr.Zero ? "не задан" : "окно TC")}, " +
                $"файл настроек «{PackerSettings.StorePath}»");

            OptionsDialog.Show(parent, dllInstance);
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ConfigurePacker: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Добавить в контейнер. Имя внутри контейнера = SubPath + имя из AddList; файл на диске =
    /// SrcPath + имя из AddList. Одно общее правило нормализации имён (ContainerPath), поэтому запись,
    /// добавленную через TC, CLI и обратно читает одинаково.
    /// </summary>
    private static int PackCore(string container, string subPath, string srcPath, List<string> names, int flags)
    {
        try
        {
            return PackBody(container, subPath, srcPath, names, flags);
        }
        finally
        {
            // Указатель на колбэк TC живёт только время нашей операции: дальше TC вправе его освободить.
            PackProgress.Clear();
        }
    }

    private static int PackBody(string container, string subPath, string srcPath, List<string> names, int flags)
    {
        if (container.Length == 0)
            return WcxApi.E_ECREATE;

        if ((flags & WcxApi.PK_PACK_ENCRYPT) != 0)
        {
            // Формат .cc шифрования не имеет: молча упаковывать без шифрования нельзя — пользователь
            // решил бы, что данные закрыты паролем.
            PluginTrace.Write("PackFiles: запрошено шифрование (PK_PACK_ENCRYPT) — формат .cc его не поддерживает");
            return WcxApi.E_NOT_SUPPORTED;
        }

        // PK_PACK_SAVE_PATHS игнорируем сознательно: путь внутри контейнера задаёт SubPath, а не
        // полный путь источника. Диск («C:») в имя записи положить нельзя в принципе.
        if ((flags & WcxApi.PK_PACK_SAVE_PATHS) != 0)
            PluginTrace.Write("PackFiles: PK_PACK_SAVE_PATHS — имя внутри контейнера берётся из SubPath, а не из полного пути");

        if (names.Count == 0)
            return WcxApi.E_NO_FILES;

        string prefix = string.Empty;
        if (subPath.Length != 0)
        {
            if (!TryEntryName(subPath, out string sub))
            {
                PluginTrace.Write($"PackFiles: SubPath нельзя положить в контейнер: '{subPath}'");
                return WcxApi.E_NOT_SUPPORTED;
            }

            prefix = sub + "\\";
        }

        var sources = new List<ContainerSource>(names.Count);
        foreach (string raw in names)
        {
            if (raw.Length == 0)
                continue;

            // AddList бывает относительным (SrcPath + имя) и полным (ANSI-ветка с сохранением путей).
            string onDisk = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : srcPath.Length == 0
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(Path.Combine(srcPath, raw.Replace('/', '\\')));

            // Полное имя с диском внутрь контейнера не положить — остаётся последний сегмент.
            string inside = prefix + (Path.IsPathRooted(raw) ? Leaf(raw) : raw);

            if (!TryEntryName(inside, out string entry))
            {
                PluginTrace.Write($"PackFiles: имя нельзя положить в контейнер: '{inside}'");
                return WcxApi.E_NOT_SUPPORTED;
            }

            sources.Add(new ContainerSource(onDisk, entry));
        }

        if (sources.Count == 0)
            return WcxApi.E_NO_FILES;

        // Настройки по умолчанию нужны только для нового контейнера: при дописывании модификатор
        // берёт размер чанка и уровень из заголовка существующего файла.
        var modifier = OpenModifier();
        ModificationReport report = modifier.Append(container, sources, PackProgress.Shared);

        PluginTrace.Write(
            $"PackFiles: '{container}' — источников {sources.Count}, добавлено записей {report.AddedEntries} " +
            $"(заменено {report.ReplacedEntries}), новых чанков {report.AddedChunks}, дописано {report.BytesWritten:N0} байт, " +
            $"резервная копия {(report.BackupPath is null ? "не требовалась" : report.BackupPath)}");

        if ((flags & WcxApi.PK_PACK_MOVE_FILES) != 0)
            MoveOriginals(sources);

        return 0;
    }

    /// <summary>
    /// Удаление из контейнера без распаковки: меняется только индекс, поэтому удаление одинаково
    /// быстрое и в файле на 5 МБ, и в файле на 10 ГБ. Освободившиеся байты остаются в файле до
    /// compact — об этом сообщает журнал, а TC показывает уменьшенный список.
    ///
    /// DeleteList приходит ровно тем, что TC видит в панели: при Del на папке это маска её
    /// содержимого — «MauiApp1\*.*», а не имя папки. Такие маски разбирает RemovalPattern:
    /// без них удаление папки возвращало бы E_NOT_SUPPORTED, и удалить папку внутри контейнера
    /// было бы нельзя вовсе. Ничего не совпало — E_NO_FILES («нет файлов для удаления»), а не
    /// «плагин не умеет».
    /// </summary>
    private static int DeleteCore(string container, List<string> names)
    {
        if (container.Length == 0)
            return WcxApi.E_EOPEN;

        if (names.Count == 0)
            return WcxApi.E_NO_FILES;

        var patterns = new List<RemovalPattern>(names.Count);
        foreach (string raw in names)
        {
            try
            {
                patterns.Add(RemovalPattern.Parse(raw));
            }
            catch (ContainerFormatException ex)
            {
                PluginTrace.Write($"DeleteFiles: имя нельзя удалить из контейнера: '{raw}' — {ex.Message}");
                return WcxApi.E_NOT_SUPPORTED;
            }
        }

        var modifier = OpenModifier();
        ModificationReport report = modifier.Remove(container, patterns);

        PluginTrace.Write(
            $"DeleteFiles: '{container}' — удалено записей {report.RemovedEntries}, дописано индексов {report.BytesWritten:N0} байт; " +
            $"мёртвых чанков {report.Health.DeadChunkCount} ({report.Health.DeadChunkBytes:N0} байт), мусор {report.Health.GarbageBytes:N0} байт " +
            $"({report.Health.GarbagePercent:F1}%) — размер файла уменьшит compact");

        return 0;
    }

    /// <summary>
    /// Те же компрессор, хэшер и сканер, что у CLI: у плагина не может быть «своей» версии формата.
    /// Уровень и размер чанка берутся из настроек диалога «Настроить» (PackerSettings), но работают
    /// они только на новый контейнер: при дописывании ContainerModifier берёт их из заголовка.
    /// </summary>
    private static ContainerModifier OpenModifier() =>
        new(new Sha256Hasher(), new SourceScanner(), PackerSettings.CurrentOptions(),
            level => new ZstdCompressor(level));

    /// <summary>
    /// Имя записи, которое формат действительно примет: та же нормализация, что у CLI и у списка
    /// панели (ContainerPath), включая предел длины 1024 символа — FileName в tHeaderDataExW именно
    /// столько и принимает, а более длинное имя TC показал бы обрезанным, и такое имя уже нельзя ни
    /// распаковать, ни удалить.
    /// </summary>
    private static bool TryEntryName(string raw, out string normalized)
    {
        normalized = string.Empty;

        try
        {
            normalized = ContainerPath.Normalize(raw);
            return normalized.Length > 0;
        }
        catch (ContainerFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// PK_PACK_MOVE_FILES: оригиналы удаляются после того, как данные легли в контейнер и индекс
    /// записан. Ошибку удаления одного файла не превращаем в ошибку всей операции: TC должен знать,
    /// что именно осталось на диске.
    /// </summary>
    private static void MoveOriginals(List<ContainerSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ContainerSource source in sources)
        {
            string root = Path.GetFullPath(source.SourceFullPath);
            if (!seen.Add(root))
                continue; // содержимое папки пришло несколькими записями: удаляем папку один раз

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                else if (File.Exists(root))
                    File.Delete(root);
            }
            catch (Exception ex)
            {
                PluginTrace.Write($"PackFiles: не удалось удалить оригинал '{root}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string Leaf(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        int slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
    }
}

/// <summary>
/// tProcessDataProc для упаковки: TC регистрирует его без handle архива (PackFiles handle не получает).
/// Slot нужен, чтобы вызов SetProcessDataProc* возвращал 0, а не код ошибки: TC считает отказ
/// plugins поломкой операции.
///
/// Прогресс сообщает ядро через IPackingProgress: ядро знает только исходный файл на диске, поэтому
/// в TC уходит именно он (тот же путь, который пользователь копирует в контейнер). Один вызов — один
/// полностью записанный файл: чанки дописываются по порядку, и файл считается записанным, когда
/// исчерпаны его последние фрагменты. Ноль в ответе TC означает «Stop» — упаковка обрывается, контейнер
/// остаётся ровно таким, каким был до операции: индекс не дописан, а значит новые чанки никем не
/// упомянуты и уйдут при следующем compact.
/// </summary>
internal sealed unsafe class PackProgress : IPackingProgress
{
    public static readonly PackProgress Shared = new();

    private static readonly object _gate = new();
    private static IntPtr _proc;
    private static bool _unicode;

    public static void Register(IntPtr proc, bool unicode)
    {
        lock (_gate)
        {
            _proc = proc;
            _unicode = unicode;
        }
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _proc = IntPtr.Zero;
            _unicode = false;
        }
    }

    public bool FilePacked(string sourceFullPath, long uncompressedBytes)
    {
        IntPtr proc;
        bool unicode;
        lock (_gate)
        {
            proc = _proc;
            unicode = _unicode;
        }

        if (proc == IntPtr.Zero)
            return true; // колбэка нет: просто упаковываем дальше

        int size = (int)Math.Clamp(uncompressedBytes, 0, int.MaxValue);

        try
        {
            if (unicode)
            {
                fixed (char* name = sourceFullPath)
                {
                    var callback = (delegate* unmanaged[Cdecl]<char*, int, int>)(void*)proc.ToPointer();
                    return callback(name, size) != 0;
                }
            }

            byte[] ansi = NativeStrings.AnsiBytes(sourceFullPath);
            if (ansi.Length == 0)
                return true; // имя не выразить в кодовой странице системы: прогресс не сообщаем

            fixed (byte* name = ansi)
            {
                var callback = (delegate* unmanaged[Cdecl]<byte*, int, int>)(void*)proc.ToPointer();
                return callback(name, size) != 0;
            }
        }
        catch (Exception ex)
        {
            // Колбэк TC — чужой код внутри нашего процесса. Его падение не должно превращать
            // упаковку в ошибку: пользователь получит готовый контейнер без полоски прогресса.
            PluginTrace.Write($"PackFiles: tProcessDataProc не отработала ({ex.GetType().Name}: {ex.Message}) — прогресс не сообщаем");
            return true;
        }
    }
}
