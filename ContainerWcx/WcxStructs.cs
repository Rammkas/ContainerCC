using System.Runtime.InteropServices;

namespace ContainerWcx;

/// <summary>
/// tOpenArchiveDataW из wcxhead.h. Порядок полей и выравнивание обязаны совпадать с C:
/// x64 → ArcName 0, OpenMode 8, OpenResult 12, CmtBuf 16, CmtBufSize 24, CmtSize 28, CmtState 32, размер 40.
/// Смещения проверяет харнесс (tools/wcx-harness) через WcxApi.Probe.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct tOpenArchiveDataW
{
    public char* ArcName;    // путь к архиву, который открыл TC
    public int OpenMode;     // PK_OM_LIST или PK_OM_EXTRACT
    public int OpenResult;   // сюда пишем причину отказа, если вернули 0
    public char* CmtBuf;     // буфер комментария архива — у контейнера комментария нет
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;     // 1 = комментария нет
}

/// <summary>
/// tHeaderDataExW из wcxhead.h. x64-раскладка: ArcName 0, FileName 2048, Flags 4096, PackSize 4100,
/// PackSizeHigh 4104, UnpSize 4108, UnpSizeHigh 4112, HostOS 4116, FileCRC 4120, FileTime 4124,
/// UnpVer 4128, Method 4132, FileAttr 4136, CmtBuf 4144 (выравнивание по 8), CmtBufSize 4152,
/// CmtSize 4156, CmtState 4160, Reserved 4164, размер 5192.
/// Reserved обязан быть обнулён (требование SDK 2.21).
/// </summary>
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
    public int FileTime;   // MS-DOS: (год-1980)<<25 | месяц<<21 | день<<16 | часы<<11 | минуты<<5 | секунды/2
    public int UnpVer;
    public int Method;
    public int FileAttr;   // DOS-атрибуты: 0x10 — папка, 0x20 — файл
    public char* CmtBuf;
    public int CmtBufSize;
    public int CmtSize;
    public int CmtState;
    public fixed byte Reserved[1024];
}

/// <summary>
/// PackDefaultParamStruct из wcxhead.h. x64-раскладка: size 0, PluginInterfaceVersionLow 4,
/// PluginInterfaceVersionHi 8, DefaultIniName 12 (ANSI, MAX_PATH=260) → размер 272, выравнивание 4.
/// Приходит в PackSetDefaultParams сразу после загрузки DLL: из него берут версию интерфейса TC
/// и путь к ini с общими настройками пакеров.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PackDefaultParamStruct
{
    public int size;
    public uint PluginInterfaceVersionLow;
    public uint PluginInterfaceVersionHi;
    public fixed byte DefaultIniName[260];
}
