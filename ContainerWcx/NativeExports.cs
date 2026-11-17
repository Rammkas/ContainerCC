using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Container_V3;
using Container_V3.Unpacking;

namespace ContainerWcx;

/// <summary>
/// Точка входа интерфейса WCX 2.21 (x64). Total Commander грузит наш DLL как нативную
/// библиотеку и вызывает экспорты по cdecl, поэтому здесь:
///   • только [UnmanagedCallersOnly] и только blittable-типы — у NativeAOT нет маршалинга;
///   • ни одно исключение не выходит за границу нативного кода: TC получает код ошибки E_*.
/// Экспортируются обе ветки wcxhead.h: Unicode (OpenArchiveW/ReadHeaderExW/ProcessFileW) и
/// «первое издание» (OpenArchive/ReadHeader/ReadHeaderEx/ProcessFile). 64-битный TC предпочитает
/// W-функции, но при отсутствии ищет ANSI (docs/64_bit_support.htm); все рабочие плагины этой
/// машины экспортируют классические имена, поэтому они должны быть и у нас.
/// </summary>
internal static class NativeExports
{
    /// <summary>WCHAR ArcName[1024] и WCHAR FileName[1024] в tHeaderDataExW.</summary>
    private const int NameChars = 1024;

    [UnmanagedCallersOnly(EntryPoint = "OpenArchiveW")]
    public static unsafe IntPtr OpenArchiveW(tOpenArchiveDataW* archiveData)
    {
        if (archiveData is null)
            return IntPtr.Zero;

        archiveData->OpenResult = 0;
        archiveData->CmtSize = 0;
        archiveData->CmtState = 1; // комментария у контейнера нет

        try
        {
            string path = NativeStrings.ReadZ(archiveData->ArcName);
            ArchiveSession session = OpenOrThrow(path);
            return ArchiveSessions.Add(session);
        }
        catch (Exception ex)
        {
            archiveData->OpenResult = WcxErrors.MapOpenError(ex);
            PluginTrace.Write($"OpenArchiveW('{NameOrNull(archiveData)}'): {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>ANSI-ветка OpenArchive: та же логика, имя читается в кодовой странице системы.</summary>
    [UnmanagedCallersOnly(EntryPoint = "OpenArchive")]
    public static unsafe IntPtr OpenArchive(tOpenArchiveData* archiveData)
    {
        if (archiveData is null)
            return IntPtr.Zero;

        archiveData->OpenResult = 0;
        archiveData->CmtSize = 0;
        archiveData->CmtState = 1; // комментария у контейнера нет

        try
        {
            string path = NativeStrings.ReadZAnsi(archiveData->ArcName);
            ArchiveSession session = OpenOrThrow(path);
            return ArchiveSessions.Add(session);
        }
        catch (Exception ex)
        {
            archiveData->OpenResult = WcxErrors.MapOpenError(ex);
            PluginTrace.Write($"OpenArchive: {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static ArchiveSession OpenOrThrow(string path)
    {
        if (path.Length == 0)
            throw new FileNotFoundException("Total Commander не передал имя контейнера.");

        ArchiveSession session = new(path);
        PluginTrace.Write($"OpenArchive: файл «{path}», записей в списке {session.EntryCount}");
        return session;
    }

    /// <summary>
    /// Список по порядку: 0 = запись записана в header, E_END_ARCHIVE = список закончен.
    /// TC читает список именно так: ReadHeaderExW → ProcessFile(PK_SKIP) → … → E_END_ARCHIVE.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ReadHeaderExW")]
    public static unsafe int ReadHeaderExW(IntPtr handle, tHeaderDataExW* header)
    {
        if (header is null)
            return WcxApi.E_SMALL_BUF;

        try
        {
            ArchiveSession? session = ArchiveSessions.Find(handle);
            if (session is null)
                return WcxApi.E_BAD_ARCHIVE;

            ListingEntry? entry = session.ReadNext();
            if (entry is null)
                return WcxApi.E_END_ARCHIVE;

            FillHeader(header, session.ContainerPath, entry, session.DosFileTime);
            return 0;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ReadHeaderExW: {ex.GetType().Name}: {ex.Message}");
            return WcxApi.E_BAD_DATA;
        }
    }

    /// <summary>ANSI-ветка ReadHeader: tHeaderData без PackSizeHigh/UnpSizeHigh (размеры до 4 ГБ).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ReadHeader")]
    public static unsafe int ReadHeader(IntPtr handle, tHeaderData* header)
    {
        if (header is null)
            return WcxApi.E_SMALL_BUF;

        try
        {
            ArchiveSession? session = ArchiveSessions.Find(handle);
            if (session is null)
                return WcxApi.E_BAD_ARCHIVE;

            ListingEntry? entry = session.ReadNext();
            if (entry is null)
                return WcxApi.E_END_ARCHIVE;

            FillHeader(header, session.ContainerPath, entry, session.DosFileTime);
            return 0;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ReadHeader: {ex.GetType().Name}: {ex.Message}");
            return WcxApi.E_BAD_DATA;
        }
    }

    /// <summary>ANSI-ветка ReadHeaderEx: tHeaderDataEx (64-битные размеры, имена в кодовой странице системы).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ReadHeaderEx")]
    public static unsafe int ReadHeaderEx(IntPtr handle, tHeaderDataEx* header)
    {
        if (header is null)
            return WcxApi.E_SMALL_BUF;

        try
        {
            ArchiveSession? session = ArchiveSessions.Find(handle);
            if (session is null)
                return WcxApi.E_BAD_ARCHIVE;

            ListingEntry? entry = session.ReadNext();
            if (entry is null)
                return WcxApi.E_END_ARCHIVE;

            FillHeader(header, session.ContainerPath, entry, session.DosFileTime);
            return 0;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ReadHeaderEx: {ex.GetType().Name}: {ex.Message}");
            return WcxApi.E_BAD_DATA;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ProcessFileW")]
    public static unsafe int ProcessFileW(IntPtr handle, int operation, char* destPath, char* destName)
    {
        try
        {
            ArchiveSession? session = ArchiveSessions.Find(handle);
            if (session is null)
                return WcxApi.E_BAD_ARCHIVE;

            ListingEntry? entry = session.Current();
            if (entry is null)
                return WcxApi.E_BAD_ARCHIVE; // ProcessFile без предварительного ReadHeaderExW

            switch (operation)
            {
                case WcxApi.PK_SKIP:
                    return 0;

                case WcxApi.PK_TEST:
                    if (!entry.IsDirectory)
                        session.Verify(entry.RelativePath);
                    return 0;

                case WcxApi.PK_EXTRACT:
                    return Extract(session, entry, NativeStrings.ReadZ(destPath), NativeStrings.ReadZ(destName));

                default:
                    return WcxApi.E_NOT_SUPPORTED;
            }
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ProcessFileW(op={operation}): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapWorkError(ex);
        }
    }

    /// <summary>ANSI-ветка ProcessFile: имена целевых путей читаются в кодовой странице системы.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ProcessFile")]
    public static unsafe int ProcessFile(IntPtr handle, int operation, byte* destPath, byte* destName)
    {
        try
        {
            ArchiveSession? session = ArchiveSessions.Find(handle);
            if (session is null)
                return WcxApi.E_BAD_ARCHIVE;

            ListingEntry? entry = session.Current();
            if (entry is null)
                return WcxApi.E_BAD_ARCHIVE;

            switch (operation)
            {
                case WcxApi.PK_SKIP:
                    return 0;

                case WcxApi.PK_TEST:
                    if (!entry.IsDirectory)
                        session.Verify(entry.RelativePath);
                    return 0;

                case WcxApi.PK_EXTRACT:
                    return Extract(session, entry, NativeStrings.ReadZAnsi(destPath), NativeStrings.ReadZAnsi(destName));

                default:
                    return WcxApi.E_NOT_SUPPORTED;
            }
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"ProcessFile(op={operation}): {ex.GetType().Name}: {ex.Message}");
            return WcxErrors.MapWorkError(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "CloseArchive")]
    public static int CloseArchive(IntPtr handle)
    {
        try
        {
            ArchiveSessions.Remove(handle)?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            PluginTrace.Write($"CloseArchive: {ex.GetType().Name}: {ex.Message}");
            return WcxApi.E_ECLOSE;
        }
    }

    /// <summary>Томов у контейнера нет: вызов принимается и игнорируется, как требует SDK 2.21.</summary>
    [UnmanagedCallersOnly(EntryPoint = "SetChangeVolProcW")]
    public static int SetChangeVolProcW(IntPtr handle, IntPtr changeVolProc) => 0;

    /// <summary>ANSI-ветка SetChangeVolProc: тоже принимается и игнорируется.</summary>
    [UnmanagedCallersOnly(EntryPoint = "SetChangeVolProc")]
    public static int SetChangeVolProc(IntPtr handle, IntPtr changeVolProc) => 0;

    /// <summary>
    /// TC передаёт указатель на tProcessDataProcW: через него плагин сообщает о прогрессе
    /// и получает отказ пользователя (возврат 0). Обязателен по SDK 2.21.
    /// handle = 0 бывает при упаковке: PackFiles handle архива не получает, поэтому колбэк
    /// регистрируется без сессии. Пустой указатель при этом остаётся отказом — это не регистрация,
    /// а бессмысленный вызов (именно так его проверяет харнесс в проходе «чужой handle»).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SetProcessDataProcW")]
    public static int SetProcessDataProcW(IntPtr handle, IntPtr processDataProc)
    {
        ArchiveSession? session = ArchiveSessions.Find(handle);
        if (session is not null)
        {
            session.SetProcessDataProc(processDataProc, unicode: true);
            return 0;
        }

        if (handle == IntPtr.Zero && processDataProc != IntPtr.Zero)
        {
            PackProgress.Register(processDataProc, unicode: true);
            return 0;
        }

        return WcxApi.E_BAD_ARCHIVE;
    }

    /// <summary>ANSI-ветка SetProcessDataProc: callback ожидает имя файла в кодовой странице системы.</summary>
    [UnmanagedCallersOnly(EntryPoint = "SetProcessDataProc")]
    public static int SetProcessDataProc(IntPtr handle, IntPtr processDataProc)
    {
        ArchiveSession? session = ArchiveSessions.Find(handle);
        if (session is not null)
        {
            session.SetProcessDataProc(processDataProc, unicode: false);
            return 0;
        }

        if (handle == IntPtr.Zero && processDataProc != IntPtr.Zero)
        {
            PackProgress.Register(processDataProc, unicode: false);
            return 0;
        }

        return WcxApi.E_BAD_ARCHIVE;
    }

    /// <summary>
    /// Возможности упаковщика: создание контейнера (NEW), изменение существующего (MODIFY),
    /// несколько файлов за один вызов PackFiles (MULTIPLE) и удаление записей (DELETE).
    /// Без NEW|MODIFY TC не вызывает PackFiles вовсе, без DELETE — не вызывает DeleteFiles, поэтому
    /// это и есть переключатель «копировать в контейнер / удалять из контейнера».
    /// OPTIONS (кнопка «Настроить» в окне упаковывания → ConfigurePacker) заявлен: диалог выбирает
    /// уровень сжатия и размер чанка для новых контейнеров. MEMPACK (PackMem) не заявлен: упаковку
    /// в память TC запрашивает только при PK_CAPS_MEMPACK.
    /// Число обязан совпадать с записью [PackerPlugins] cc=… в wincmd.ini: TC сверяет их, и при
    /// расхождении сообщает о неподходящих возможностях плагина (смена числа требует переустановки).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "GetPackerCaps")]
    public static int GetPackerCaps()
    {
        PluginTrace.Write($"GetPackerCaps(): {WcxApi.PackerCaps} (NEW|MODIFY|MULTIPLE|DELETE|OPTIONS) — упаковщик .cc загружен Total Commander");
        return WcxApi.PackerCaps;
    }

    [UnmanagedCallersOnly(EntryPoint = "GetBackgroundFlags")]
    public static int GetBackgroundFlags()
    {
        PluginTrace.Write("GetBackgroundFlags(): 0 — фоновые операции не заявлены");
        return 0;
    }

    private static int Extract(ArchiveSession session, ListingEntry entry, string destPath, string destName)
    {
        if (entry.IsDirectory)
        {
            string directory = Combine(destPath, destName, entry);
            if (directory.Length == 0)
                return WcxApi.E_ECREATE;

            session.CreateDirectory(Path.GetFullPath(directory));
            return 0;
        }

        string target = Combine(destPath, destName, entry);
        if (target.Length == 0)
            return WcxApi.E_ECREATE;

        if (session.ReportProgress(entry) == 0)
            return WcxApi.E_EABORTED; // пользователь остановил операцию

        session.ExtractTo(entry.RelativePath, target);
        return 0;
    }

    /// <summary>
    /// TC передаёт либо полный путь в DestName (тогда DestPath = NULL), либо имя файла и папку отдельно.
    /// Если имени нет вообще, берём имя из индекса: «распакуй в эту папку» тоже корректная постановка.
    /// </summary>
    private static string Combine(string destPath, string destName, ListingEntry entry)
    {
        if (destName.Length == 0)
            return destPath.Length == 0 ? string.Empty : Path.Combine(destPath, Leaf(entry.RelativePath));

        return destPath.Length == 0 ? destName : Path.Combine(destPath, destName);
    }

    private static unsafe void FillHeader(tHeaderDataExW* header, string archivePath, ListingEntry entry, int dosFileTime)
    {
        // WCX 2.21: Reserved обязан быть обнулён — обнуляем всю структуру, затем заполняем нужное.
        new Span<byte>(header, Unsafe.SizeOf<tHeaderDataExW>()).Clear();

        NativeStrings.WriteZ(header->ArcName, NameChars, archivePath);
        NativeStrings.WriteZ(header->FileName, NameChars, entry.RelativePath);

        header->Flags = 0;
        header->PackSize = (uint)(entry.PackedSize & 0xFFFFFFFF);
        header->PackSizeHigh = (uint)(entry.PackedSize >> 32);
        header->UnpSize = (uint)(entry.Size & 0xFFFFFFFF);
        header->UnpSizeHigh = (uint)(entry.Size >> 32);
        header->HostOS = 0;  // в SDK: 0; значение важно только для совместимости с unrar
        header->FileCRC = 0; // CRC контейнер не хранит: целостность проверяется хэшем чанка при чтении
        header->FileTime = dosFileTime;
        header->UnpVer = 0;
        header->Method = 0;  // у формата своя компрессия: колонку «Method» TC показать не может осмысленно
        // Папка отличается от файла только FileAttr 0x10: завершающий «\» к имени не добавляем,
        // так же делает реальный 64-битный пакер (Total7zip.wcx64 — см. tools/wcx-harness).
        header->FileAttr = entry.IsDirectory ? WcxApi.FA_DIRECTORY : WcxApi.FA_ARCHIVE;
        // SDK о Cmt*: «currently not used in Total Commander, so they may be set to zero».
        header->CmtBuf = null;
        header->CmtBufSize = 0;
        header->CmtSize = 0;
        header->CmtState = 0;
    }

    /// <summary>Заполнение tHeaderData (ANSI, «первое издание»): те же значения, имена в кодовой странице системы.</summary>
    private static unsafe void FillHeader(tHeaderData* header, string archivePath, ListingEntry entry, int dosFileTime)
    {
        new Span<byte>(header, Unsafe.SizeOf<tHeaderData>()).Clear();

        NativeStrings.WriteZAnsi(header->ArcName, 260, archivePath);
        NativeStrings.WriteZAnsi(header->FileName, 260, entry.RelativePath);

        header->Flags = 0;
        // В этой структуре нет PackSizeHigh/UnpSizeHigh: файл крупнее 4 ГБ ANSI-ветка покажет неверно,
        // таковы ограничения «первого издания»; полная картина — через W-функции.
        header->PackSize = unchecked((int)(uint)(entry.PackedSize & 0xFFFFFFFF));
        header->UnpSize = unchecked((int)(uint)(entry.Size & 0xFFFFFFFF));
        header->HostOS = 0;
        header->FileCRC = 0;
        header->FileTime = dosFileTime;
        header->UnpVer = 0;
        header->Method = 0;
        header->FileAttr = entry.IsDirectory ? WcxApi.FA_DIRECTORY : WcxApi.FA_ARCHIVE;
        header->CmtBuf = null;
        header->CmtBufSize = 0;
        header->CmtSize = 0;
        header->CmtState = 0;
    }

    /// <summary>Заполнение tHeaderDataEx (ANSI): 64-битные размеры есть, имена — в кодовой странице системы.</summary>
    private static unsafe void FillHeader(tHeaderDataEx* header, string archivePath, ListingEntry entry, int dosFileTime)
    {
        new Span<byte>(header, Unsafe.SizeOf<tHeaderDataEx>()).Clear();

        NativeStrings.WriteZAnsi(header->ArcName, 1024, archivePath);
        NativeStrings.WriteZAnsi(header->FileName, 1024, entry.RelativePath);

        header->Flags = 0;
        header->PackSize = (uint)(entry.PackedSize & 0xFFFFFFFF);
        header->PackSizeHigh = (uint)(entry.PackedSize >> 32);
        header->UnpSize = (uint)(entry.Size & 0xFFFFFFFF);
        header->UnpSizeHigh = (uint)(entry.Size >> 32);
        header->HostOS = 0;
        header->FileCRC = 0;
        header->FileTime = dosFileTime;
        header->UnpVer = 0;
        header->Method = 0;
        header->FileAttr = entry.IsDirectory ? WcxApi.FA_DIRECTORY : WcxApi.FA_ARCHIVE;
        header->CmtBuf = null;
        header->CmtBufSize = 0;
        header->CmtSize = 0;
        header->CmtState = 0;
    }

    private static string Leaf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash >= 0 ? relativePath.Substring(slash + 1) : relativePath;
    }

    private static unsafe string NameOrNull(tOpenArchiveDataW* archiveData)
    {
        try
        {
            return NativeStrings.ReadZ(archiveData->ArcName);
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
