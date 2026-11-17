using System.Runtime.InteropServices;

namespace WcxHarness;

/// <summary>
/// Копия структур wcxhead.h, написанная независимо от плагина — по официальному SDK.
/// Если харнесс видит поля там же, где их пишет плагин, значит раскладка совпала с C.
/// Смещения для x64 зафиксированы константами и проверяются в CheckLayout().
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tOpenArchiveDataW
{
    public char* ArcName;
    public int OpenMode;
    public int OpenResult;
    public char* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tHeaderDataExW
{
    public fixed char ArcName[1024];
    public fixed char FileName[1024];
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
    public char* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
    public fixed byte Reserved[1024];
}

/// <summary>Константы WCX 2.21 (wcxhead.h) — продублированы здесь намеренно.</summary>
internal static class Wcx
{
    public const int PK_OM_LIST = 0;
    public const int PK_OM_EXTRACT = 1;
    public const int PK_SKIP = 0;
    public const int PK_TEST = 1;
    public const int PK_EXTRACT = 2;

    public const int E_END_ARCHIVE = 10;
    public const int E_BAD_DATA = 12;
    public const int E_BAD_ARCHIVE = 13;
    public const int E_UNKNOWN_FORMAT = 14;
    public const int E_EOPEN = 15;
    public const int E_EABORTED = 21;
    public const int E_NOT_SUPPORTED = 24;

    public const int FA_DIRECTORY = 0x10;
    public const int FA_ARCHIVE = 0x20;

    // tOpenArchiveDataW (40 байт)
    public const int SizeOfOpenData = 40;
    public const int OffOpenArcName = 0;
    public const int OffOpenMode = 8;
    public const int OffOpenResult = 12;
    public const int OffOpenCmtBuf = 16;
    public const int OffOpenCmtBufSize = 24;
    public const int OffOpenCmtSize = 28;
    public const int OffOpenCmtState = 32;

    // tHeaderDataExW (5192 байта)
    public const int SizeOfHeader = 5192;
    public const int OffArcName = 0;
    public const int OffFileName = 2048;
    public const int OffFlags = 4096;
    public const int OffPackSize = 4100;
    public const int OffPackSizeHigh = 4104;
    public const int OffUnpSize = 4108;
    public const int OffUnpSizeHigh = 4112;
    public const int OffHostOS = 4116;
    public const int OffFileCRC = 4120;
    public const int OffFileTime = 4124;
    public const int OffUnpVer = 4128;
    public const int OffMethod = 4132;
    public const int OffFileAttr = 4136;
    public const int OffCmtBuf = 4144;
    public const int OffCmtBufSize = 4152;
    public const int OffCmtSize = 4156;
    public const int OffCmtState = 4160;
    public const int OffReserved = 4164;

    public static string ErrorName(int code) => code switch
    {
        0 => "OK",
        E_END_ARCHIVE => "E_END_ARCHIVE",
        11 => "E_NO_MEMORY",
        E_BAD_DATA => "E_BAD_DATA",
        E_BAD_ARCHIVE => "E_BAD_ARCHIVE",
        E_UNKNOWN_FORMAT => "E_UNKNOWN_FORMAT",
        E_EOPEN => "E_EOPEN",
        16 => "E_ECREATE",
        17 => "E_ECLOSE",
        18 => "E_EREAD",
        19 => "E_EWRITE",
        20 => "E_SMALL_BUF",
        E_EABORTED => "E_EABORTED",
        22 => "E_NO_FILES",
        23 => "E_TOO_MANY_FILES",
        E_NOT_SUPPORTED => "E_NOT_SUPPORTED",
        _ => $"код {code}",
    };

    // ─────────────────── упаковка: флаги PackFiles и возможности из GetPackerCaps ───────────────────

    public const int E_NO_FILES = 22;
    public const int E_TOO_MANY_FILES = 23;

    public const int PK_PACK_MOVE_FILES = 1;
    public const int PK_PACK_SAVE_PATHS = 2;
    public const int PK_PACK_ENCRYPT = 4;

    public const int PK_CAPS_NEW = 1;
    public const int PK_CAPS_MODIFY = 2;
    public const int PK_CAPS_MULTIPLE = 4;
    public const int PK_CAPS_DELETE = 8;
    public const int PK_CAPS_OPTIONS = 16;
    public const int PK_CAPS_MEMPACK = 32;
    public const int PK_CAPS_BY_CONTENT = 64;

    /// <summary>Размер PackDefaultParamStruct: 4 + 4 + 4 + MAX_PATH(260).</summary>
    public const int SizeOfPackDefaults = 272;

    /// <summary>Что именно заявил плагин — для отчёта и для проверок упаковки/удаления.</summary>
    public static string CapsName(int caps)
    {
        if (caps == 0)
            return "0 (только распаковка)";

        var names = new List<string>();
        if ((caps & PK_CAPS_NEW) != 0) names.Add("NEW");
        if ((caps & PK_CAPS_MODIFY) != 0) names.Add("MODIFY");
        if ((caps & PK_CAPS_MULTIPLE) != 0) names.Add("MULTIPLE");
        if ((caps & PK_CAPS_DELETE) != 0) names.Add("DELETE");
        if ((caps & PK_CAPS_OPTIONS) != 0) names.Add("OPTIONS");
        if ((caps & PK_CAPS_MEMPACK) != 0) names.Add("MEMPACK");
        if ((caps & PK_CAPS_BY_CONTENT) != 0) names.Add("BY_CONTENT");
        if ((caps & ~0x1FF) != 0) names.Add("прочие биты");

        return $"{caps} ({string.Join("|", names)})";
    }
}

/// <summary>Одна строка списка архива, как её увидел харнесс.</summary>
internal sealed record ArcEntry(string Name, long UnpSize, long PackSize, int FileAttr, int FileTime, bool IsDirectory);

/// <summary>
/// PackDefaultParamStruct из wcxhead.h: приходит в PackSetDefaultParams сразу после загрузки DLL.
/// x64-раскладка: size 0, версия интерфейса 4 и 8, DefaultIniName 12 (ANSI, MAX_PATH) → 272 байта.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tPackDefaultParamStruct
{
    public int size;
    public uint PluginInterfaceVersionLow;
    public uint PluginInterfaceVersionHi;
    public fixed byte DefaultIniName[260];
}
