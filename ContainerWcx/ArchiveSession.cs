using System;
using System.Collections.Generic;
using System.IO;
using Container_V3.Compressors;
using Container_V3.Hashers;
using Container_V3.Unpacking;

namespace ContainerWcx;

/// <summary>
/// Одна сессия OpenArchive → ReadHeaderEx* → ProcessFile* → CloseArchive.
/// TC держит выданный handle между вызовами, поэтому список и курсор живут здесь.
/// </summary>
internal sealed class ArchiveSession : IDisposable
{
    private readonly ContainerUnpacker _unpacker;
    private readonly List<ListingEntry> _entries;
    private readonly object _gate = new();
    private IntPtr _processDataProc;
    private bool _processDataProcIsUnicode;
    private int _cursor;

    public ArchiveSession(string containerPath)
    {
        ContainerPath = containerPath;

        // Те же компрессор и хэшер, что у CLI: у плагина не может быть «своей» версии формата.
        _unpacker = new ContainerUnpacker(containerPath, new ZstdCompressor(), new Sha256Hasher());

        // ReadIndex кэшируется внутри распаковщика: дальнейшие выборочные операции индекс уже не читают.
        _entries = ArchiveListing.Build(_unpacker.ReadIndex());

        DosFileTime = NativeStrings.ToDosTime(SafeWriteTime(containerPath));
    }

    private static DateTime SafeWriteTime(string containerPath)
    {
        try
        {
            return File.GetLastWriteTime(containerPath);
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    public string ContainerPath { get; }

    /// <summary>
    /// Время для панели TC. Индекс V3 не хранит ни времени, ни атрибутов файлов, поэтому
    /// показывается время самого контейнера: одинаковое при каждом открытии, а не «сейчас».
    /// </summary>
    public int DosFileTime { get; }

    public int EntryCount => _entries.Count;

    /// <summary>Следующая запись списка. null — список закончен (TC ждёт E_END_ARCHIVE).</summary>
    public ListingEntry? ReadNext()
    {
        lock (_gate)
        {
            if (_cursor >= _entries.Count)
                return null;

            return _entries[_cursor++];
        }
    }

    /// <summary>Запись, которую вернул последний ReadHeaderExW: ProcessFile всегда работает по ней.</summary>
    public ListingEntry? Current()
    {
        lock (_gate)
        {
            if (_cursor <= 0 || _cursor > _entries.Count)
                return null;

            return _entries[_cursor - 1];
        }
    }

    public void SetProcessDataProc(IntPtr proc, bool unicode)
    {
        lock (_gate)
        {
            _processDataProc = proc;
            _processDataProcIsUnicode = unicode;
        }
    }

    /// <summary>
    /// Прогресс для панели TC (tProcessDataProcW / tProcessDataProc). Один вызов на файл: распаковщик
    /// собирает файл одной операцией, дробить размер по байтам смысла нет. Возврат 0 = пользователь нажал Stop.
    /// Знак имени (W или ANSI) берётся от того, какая ветка функций включила этот callback.
    /// </summary>
    public unsafe int ReportProgress(ListingEntry entry)
    {
        IntPtr proc;
        bool unicode;
        lock (_gate)
        {
            proc = _processDataProc;
            unicode = _processDataProcIsUnicode;
        }

        if (proc == IntPtr.Zero)
            return 1;

        string name = entry.RelativePath;
        int size = (int)Math.Clamp(entry.Size, 0, int.MaxValue);

        if (unicode)
        {
            fixed (char* pointer = name)
            {
                var callback = (delegate* unmanaged[Cdecl]<char*, int, int>)(void*)proc.ToPointer();
                return callback(pointer, size);
            }
        }

        byte[] ansi = NativeStrings.AnsiBytes(name);
        if (ansi.Length == 0)
            return 1; // имя не выразить в кодовой странице системы: прогресс не сообщаем

        fixed (byte* pointer = ansi)
        {
            var callback = (delegate* unmanaged[Cdecl]<byte*, int, int>)(void*)proc.ToPointer();
            return callback(pointer, size);
        }
    }

    /// <summary>Проверка целостности файла: хэши чанков и декомпрессия, на диск ничего не пишется.</summary>
    public void Verify(string relativePath)
    {
        lock (_gate)
            _unpacker.VerifyFile(relativePath);
    }

    public void ExtractTo(string relativePath, string absoluteTargetFile)
    {
        lock (_gate)
            _unpacker.UnpackFileTo(relativePath, absoluteTargetFile);
    }

    public void CreateDirectory(string absoluteTargetDirectory)
    {
        lock (_gate)
            Directory.CreateDirectory(absoluteTargetDirectory);
    }

    public void Dispose() => _unpacker.Dispose();
}
