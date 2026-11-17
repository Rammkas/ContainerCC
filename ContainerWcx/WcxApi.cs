namespace ContainerWcx;

/// <summary>
/// Константы интерфейса WCX 2.21 (Total Commander packer plugin API).
/// Значения — из официального SDK (ghisler/WCX-SDK: docs/*.htm и src/wcxhead.h).
/// Структуры описаны в WcxStructs.cs.
/// </summary>
internal static class WcxApi
{
    // Режимы OpenArchive
    public const int PK_OM_LIST = 0;
    public const int PK_OM_EXTRACT = 1;

    // Операции ProcessFile
    public const int PK_SKIP = 0;
    public const int PK_TEST = 1;
    public const int PK_EXTRACT = 2;

    // Коды ошибок, которые понимает TC
    public const int E_END_ARCHIVE = 10;
    public const int E_NO_MEMORY = 11;
    public const int E_BAD_DATA = 12;
    public const int E_BAD_ARCHIVE = 13;
    public const int E_UNKNOWN_FORMAT = 14;
    public const int E_EOPEN = 15;
    public const int E_ECREATE = 16;
    public const int E_ECLOSE = 17;
    public const int E_EREAD = 18;
    public const int E_EWRITE = 19;
    public const int E_SMALL_BUF = 20;
    public const int E_EABORTED = 21;
    public const int E_NO_FILES = 22;
    public const int E_TOO_MANY_FILES = 23;
    public const int E_NOT_SUPPORTED = 24;

    // DOS-атрибуты для tHeaderDataExW.FileAttr
    public const int FA_DIRECTORY = 0x10;
    public const int FA_ARCHIVE = 0x20;

    // Флаги PackFiles (wcxhead.h)
    public const int PK_PACK_MOVE_FILES = 1;   // «переместить»: удалить оригиналы после упаковки
    public const int PK_PACK_SAVE_PATHS = 2;   // сохранять полные пути — путь внутри контейнера задаёт SubPath
    public const int PK_PACK_ENCRYPT = 4;      // шифровать пакет — у .cc шифрования нет

    // Возможности из GetPackerCaps (wcxhead.h)
    public const int PK_CAPS_NEW = 1;          // умеет создавать архив (PackFiles с несуществующим PackedFile)
    public const int PK_CAPS_MODIFY = 2;       // умеет менять существующий архив
    public const int PK_CAPS_MULTIPLE = 4;     // PackFiles получает несколько файлов за один вызов
    public const int PK_CAPS_DELETE = 8;       // DeleteFiles
    public const int PK_CAPS_OPTIONS = 16;     // ConfigurePacker (диалог настроек пакета) — заявляем
    public const int PK_CAPS_MEMPACK = 32;     // PackMem — не заявляем
    public const int PK_CAPS_BY_CONTENT = 64;  // определение архива по содержимому — не заявляем
    public const int PK_CAPS_SEARCHTEXT = 128; // не заявляем
    public const int PK_CAPS_HIDE = 256;       // не заявляем
    public const int PK_CAPS_ENCRYPT = 512;    // шифрование — не заявляем

    /// <summary>
    /// Что заявляем: создание контейнера, его изменение, несколько файлов за один вызов, удаление
    /// и диалог настроек пакета (PK_CAPS_OPTIONS — кнопка «Настроить» в окне упаковывания,
    /// ConfigurePacker). Без NEW|MODIFY TC не вызывает PackFiles вовсе, без DELETE — не вызывает
    /// DeleteFiles, без OPTIONS — не вызывает ConfigurePacker и не показывает кнопку «Настроить».
    /// TOC (tools/install-tc-plugin.ps1) обязан записать в [PackerPlugins] ровно это число: TC
    /// сверяет запись ini с ответом GetPackerCaps, и несоответствие — «plugin has wrong capabilities».
    /// После смены caps плагин нужно переустановить: TC читает возможности из ini при установке.
    /// </summary>
    public const int PackerCaps =
        PK_CAPS_NEW | PK_CAPS_MODIFY | PK_CAPS_MULTIPLE | PK_CAPS_DELETE | PK_CAPS_OPTIONS; // 31
}
