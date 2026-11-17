using System.Runtime.InteropServices;

namespace WcxHarness;

/// <summary>
/// Копия ANSI-структур «первого издания» из wcxhead.h (OpenArchive / ReadHeader / ReadHeaderEx /
/// ProcessFile / SetProcessDataProc / SetChangeVolProc — без суффикса W), написанная независимо от
/// плагина. Смещения x64 зафиксированы числами в WcxAnsi: харнесс читает имена, размеры и атрибуты
/// прямо по этим числам, поэтому любая разница раскладки плагина и SDK видна как мусор в полях,
/// а не как молчаливое «повезло».
///
/// Важно: у ANSI-заголовка tHeaderData нет полей PackSizeHigh/UnpSizeHigh — размеры старше 4 ГБ
/// «первое издание» выразить не может (PackSize/UnpSize там signed int, а в tHeaderDataEx —
/// unsigned int). Имя архива в tOpenArchiveData по wcxhead.h — указатель char* ArcName, как и в
/// W-версии; inline-массивы char[260]/char[1024] есть только в tHeaderData и tHeaderDataEx.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tOpenArchiveData
{
    public byte* ArcName;   // путь к архиву в кодовой странице системы (CP_ACP)
    public int OpenMode;    // PK_OM_LIST или PK_OM_EXTRACT
    public int OpenResult;  // причина отказа, если вернули 0
    public byte* CmtBuf;    // ANSI-буфер комментария архива
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;    // 1 = комментария нет
}

/// <summary>tHeaderData («первое издание», ANSI): 584 байта, размеры только 32-битные.</summary>
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
    public byte* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
}

/// <summary>tHeaderDataEx (ANSI): 3144 байта, 64-битные размеры есть, имена — 1024 байта в CP_ACP.</summary>
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
    public byte* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
    public fixed byte Reserved[1024];
}

/// <summary>
/// Смещения и размеры ANSI-структур для x64. Получены из порядка полей и правил выравнивания C
/// (x64: указатель — 8 байт и выравнивание по 8, int — 4), проверяются по Unsafe.SizeOf/Unsafe.Add
/// в CheckAnsiLayout().
/// </summary>
internal static class WcxAnsi
{
    // tOpenArchiveData (ANSI): ArcName 0, OpenMode 8, OpenResult 12, CmtBuf 16, CmtBufSize 24,
    // CmtSize 28, CmtState 32, размер 40 — численно как у W-версии.
    public const int SizeOfOpenData = 40;
    public const int OffOpenArcName = 0;
    public const int OffOpenMode = 8;
    public const int OffOpenResult = 12;
    public const int OffOpenCmtBuf = 16;
    public const int OffOpenCmtBufSize = 24;
    public const int OffOpenCmtSize = 28;
    public const int OffOpenCmtState = 32;

    // tHeaderData (ANSI, 584 байта): ArcName 0, FileName 260, Flags 520, PackSize 524, UnpSize 528,
    // HostOS 532, FileCRC 536, FileTime 540, UnpVer 544, Method 548, FileAttr 552,
    // CmtBuf 560 (выравнивание по 8 после int FileAttr), CmtBufSize 568, CmtSize 572, CmtState 576.
    public const int SizeOfHeader = 584;
    public const int NameBytes = 260;
    public const int OffArcName = 0;
    public const int OffFileName = 260;
    public const int OffFlags = 520;
    public const int OffPackSize = 524;
    public const int OffUnpSize = 528;
    public const int OffHostOS = 532;
    public const int OffFileCRC = 536;
    public const int OffFileTime = 540;
    public const int OffUnpVer = 544;
    public const int OffMethod = 548;
    public const int OffFileAttr = 552;
    public const int OffCmtBuf = 560;
    public const int OffCmtBufSize = 568;
    public const int OffCmtSize = 572;
    public const int OffCmtState = 576;

    // tHeaderDataEx (ANSI, 3144 байта): ArcName 0, FileName 1024, Flags 2048, PackSize 2052,
    // PackSizeHigh 2056, UnpSize 2060, UnpSizeHigh 2064, HostOS 2068, FileCRC 2072, FileTime 2076,
    // UnpVer 2080, Method 2084, FileAttr 2088, CmtBuf 2096 (выравнивание по 8), CmtBufSize 2104,
    // CmtSize 2108, CmtState 2112..2115, Reserved 2116..3139 (byte-массив не требует выравнивания,
    // поэтому идёт сразу после CmtState), хвостовое выравнивание 3140..3143 → размер 3144.
    public const int SizeOfHeaderEx = 3144;
    public const int ExNameBytes = 1024;
    public const int ExReservedBytes = 1024;
    public const int OffExArcName = 0;
    public const int OffExFileName = 1024;
    public const int OffExFlags = 2048;
    public const int OffExPackSize = 2052;
    public const int OffExPackSizeHigh = 2056;
    public const int OffExUnpSize = 2060;
    public const int OffExUnpSizeHigh = 2064;
    public const int OffExHostOS = 2068;
    public const int OffExFileCRC = 2072;
    public const int OffExFileTime = 2076;
    public const int OffExUnpVer = 2080;
    public const int OffExMethod = 2084;
    public const int OffExFileAttr = 2088;
    public const int OffExCmtBuf = 2096; // после int FileAttr (2088) — выравнивание по 8, хвост 2092..2095
    public const int OffExCmtBufSize = 2104;
    public const int OffExCmtSize = 2108;
    public const int OffExCmtState = 2112;
    public const int OffExReserved = 2116; // сразу после CmtState (2112..2115): byte-массив выравнивания не просит
}

/// <summary>
/// ANSI = кодовая страница системы (CP_ACP = 1251 на этой машине). Харнесс делает ровно то же
/// преобразование, что Win32-хост Total Commander: kernel32 WideCharToMultiByte/MultiByteToWideChar.
/// Так проверка не зависит от таблиц .NET (харнесс собран с InvariantGlobalization).
/// </summary>
internal static unsafe class AnsiCodePage
{
    private const uint CpAcp = 0;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint flags, byte* multiByte, int byteCount, char* wideChar, int wideChars);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, char* wideChar, int wideChars, byte* multiByte, int byteCount,
        byte* defaultChar, byte* usedDefaultChar);

    /// <summary>string → байты CP_ACP и завершающий 0 (буфер годится для char* параметра WCX).</summary>
    public static byte[] ToAnsiZ(string value)
    {
        fixed (char* wide = value)
        {
            int needed = WideCharToMultiByte(CpAcp, 0, wide, value.Length, null, 0, null, null);
            byte[] buffer = new byte[Math.Max(needed, 1) + 1]; // +1 — заведомо гарантируем завершающий 0

            fixed (byte* ansi = buffer)
                WideCharToMultiByte(CpAcp, 0, wide, value.Length, ansi, buffer.Length, null, null);

            return buffer;
        }
    }

    /// <summary>То же, но без завершающего 0 — для побайтового сравнения с именем в заголовке.</summary>
    public static byte[] ToAnsi(string value)
    {
        byte[] withZero = ToAnsiZ(value);
        int length = withZero.Length;
        while (length > 0 && withZero[length - 1] == 0)
            length--;

        if (length == withZero.Length)
            return withZero;

        byte[] trimmed = new byte[length];
        Array.Copy(withZero, trimmed, length);
        return trimmed;
    }

    /// <summary>char* (ANSI) → строка: байты CP_ACP декодируются в UTF-16. null → пустая строка.</summary>
    public static string FromAnsi(byte* ansi, int maxBytes)
    {
        if (ansi == null)
            return string.Empty;

        int length = 0;
        while (length < maxBytes && ansi[length] != 0)
            length++;

        if (length == 0)
            return string.Empty;

        int needed = MultiByteToWideChar(CpAcp, 0, ansi, length, null, 0);
        if (needed <= 0)
            return string.Empty; // не выводится в CP_ACP — вернём пустоту, сравнение это заметит

        char[] wide = new char[needed];
        fixed (char* pointer = wide)
        {
            int written = MultiByteToWideChar(CpAcp, 0, ansi, length, pointer, needed);
            return written <= 0 ? string.Empty : new string(pointer, 0, written);
        }
    }

    /// <summary>Копия байтов ANSI-имени (для сравнения «как написано» и для отчёта).</summary>
    public static byte[] SnapshotBytes(byte* ansi, int maxBytes)
    {
        if (ansi == null)
            return Array.Empty<byte>();

        int length = 0;
        while (length < maxBytes && ansi[length] != 0)
            length++;

        byte[] copy = new byte[length];
        for (int i = 0; i < length; i++)
            copy[i] = ansi[i];

        return copy;
    }

    public static int CurrentCodePage()
    {
        // GetACP() из kernel32: какое CP_ACP (=0) значит на этой машине.
        return GetACP();
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetACP();
}
