using System.Runtime.InteropServices;

namespace ContainerWcx;

/// <summary>
/// Структуры «первого издания» WCX (ANSI) из wcxhead.h: TC 64-bit использует их, если не находит
/// W-функций (docs/64_bit_support.htm). Экспортируем обе ветки, как это делают рабочие плагины
/// этой машины (iso.wcx64, hlp.wcx64, btdir.wcx64 экспортируют и ANSI, и W).
///
/// x64-раскладка tHeaderData (ANSI): ArcName 0..259, FileName 260..519, Flags 520, PackSize 524,
/// UnpSize 528, HostOS 532, FileCRC 536, FileTime 540, UnpVer 544, Method 548, FileAttr 552,
/// CmtBuf 560 (выравнивание по 8), CmtBufSize 568, CmtSize 572, CmtState 576, размер 584.
/// У этой структуры нет PackSizeHigh/UnpSizeHigh: размеры старше 4 ГБ ANSI-ветка не показывает.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tHeaderData
{
    public fixed byte ArcName[260];
    public fixed byte FileName[260];
    public int Flags;
    public int PackSize;
    public int UnpSize;
    public int HostOS;
    public int FileCRC;
    public int FileTime;
    public int UnpVer;
    public int Method;
    public int FileAttr;
    public byte* CmtBuf;     // ANSI-буфер комментария; не используется, всегда null
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
}

/// <summary>
/// x64-раскладка tHeaderDataEx (ANSI): ArcName 0..1023, FileName 1024..2047, Flags 2048,
/// PackSize 2052, PackSizeHigh 2056, UnpSize 2060, UnpSizeHigh 2064, HostOS 2068, FileCRC 2072,
/// FileTime 2076, UnpVer 2080, Method 2084, FileAttr 2088, CmtBuf 2096, CmtBufSize 2104,
/// CmtSize 2108, CmtState 2112..2115, Reserved 2116..3139 (byte-массив идёт сразу после CmtState,
/// выравнивания не просит), размер 3144. Смещения подтверждены харнессом (проходы 7–13).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tHeaderDataEx
{
    public fixed byte ArcName[1024];
    public fixed byte FileName[1024];
    public int Flags;
    public uint PackSize;
    public uint PackSizeHigh;
    public uint UnpSize;
    public uint UnpSizeHigh;
    public int HostOS;
    public int FileCRC;
    public int FileTime;
    public int UnpVer;
    public int Method;
    public int FileAttr;
    public byte* CmtBuf;     // ANSI-буфер комментария; не используется, всегда null
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
    public fixed byte Reserved[1024];
}

/// <summary>
/// x64-раскладка tOpenArchiveData (ANSI): ArcName 0, OpenMode 8, OpenResult 12, CmtBuf 16,
/// CmtBufSize 24, CmtSize 28, CmtState 32, размер 40 — та же, что у W-версии.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tOpenArchiveData
{
    public byte* ArcName;   // ANSI-путь к архиву (кодовая страница системы)
    public int OpenMode;
    public int OpenResult;
    public byte* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
}
