using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ContainerWcx;

/// <summary>
/// Handle-таблица сессий. Идентификаторы начинаются с 1, потому что 0 для TC означает «не открыто»
/// (в OpenArchiveW при ошибке возвращают 0 и заполняют OpenResult).
/// Таблица переживает повторный CloseArchive и случайный «чужой» handle: TryRemove/TryGetValue
/// просто не находят сессию и возвращают код ошибки вместо падения.
/// </summary>
internal static class ArchiveSessions
{
    private static readonly ConcurrentDictionary<IntPtr, ArchiveSession> Table = new();
    private static long _nextId;

    public static IntPtr Add(ArchiveSession session)
    {
        IntPtr handle = new(Interlocked.Increment(ref _nextId));
        Table[handle] = session;
        return handle;
    }

    public static ArchiveSession? Find(IntPtr handle)
        => Table.TryGetValue(handle, out ArchiveSession? session) ? session : null;

    public static ArchiveSession? Remove(IntPtr handle)
        => Table.TryRemove(handle, out ArchiveSession? session) ? session : null;
}
