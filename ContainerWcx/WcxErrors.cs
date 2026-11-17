using Container_V3;

namespace ContainerWcx;

/// <summary>
/// Перевод ошибок .NET в коды, которые понимает Total Commander. Граница нативная: исключение не
/// должно перейти в TC — NativeAOT не превращает необработанное исключение в код ошибки, а чужой
/// нативный стек не знает, что с ним делать.
///
/// Порядок веток важен: NothingToRemoveException наследует ContainerFormatException, но для TC это
/// разные ситуации — «в архиве такого нет» (E_NO_FILES) против «архив не читается» (E_UNKNOWN_FORMAT).
/// </summary>
internal static class WcxErrors
{
    /// <summary>Человеческое имя кода ошибки — только для журнала плагина (PluginTrace).</summary>
    public static string Name(int code) => code switch
    {
        0 => "OK",
        WcxApi.E_END_ARCHIVE => "E_END_ARCHIVE",
        WcxApi.E_NO_MEMORY => "E_NO_MEMORY",
        WcxApi.E_BAD_DATA => "E_BAD_DATA",
        WcxApi.E_BAD_ARCHIVE => "E_BAD_ARCHIVE",
        WcxApi.E_UNKNOWN_FORMAT => "E_UNKNOWN_FORMAT",
        WcxApi.E_EOPEN => "E_EOPEN",
        WcxApi.E_ECREATE => "E_ECREATE",
        WcxApi.E_ECLOSE => "E_ECLOSE",
        WcxApi.E_EREAD => "E_EREAD",
        WcxApi.E_EWRITE => "E_EWRITE",
        WcxApi.E_SMALL_BUF => "E_SMALL_BUF",
        WcxApi.E_EABORTED => "E_EABORTED",
        WcxApi.E_NO_FILES => "E_NO_FILES",
        WcxApi.E_TOO_MANY_FILES => "E_TOO_MANY_FILES",
        WcxApi.E_NOT_SUPPORTED => "E_NOT_SUPPORTED",
        _ => $"код {code}",
    };

    /// <summary>Ошибки открытия архива.</summary>
    public static int MapOpenError(Exception ex) => ex switch
    {
        ContainerFormatException => WcxApi.E_UNKNOWN_FORMAT,
        FileNotFoundException => WcxApi.E_EOPEN,
        DirectoryNotFoundException => WcxApi.E_EOPEN,
        UnauthorizedAccessException => WcxApi.E_EOPEN,
        IOException => WcxApi.E_EOPEN,
        OutOfMemoryException => WcxApi.E_NO_MEMORY,
        _ => WcxApi.E_BAD_ARCHIVE,
    };

    /// <summary>Ошибки чтения и распаковки (ProcessFile, ReadHeaderEx).</summary>
    public static int MapWorkError(Exception ex) => ex switch
    {
        ContainerFormatException => WcxApi.E_UNKNOWN_FORMAT,
        ChunkIntegrityException => WcxApi.E_BAD_DATA,
        ManifestMismatchException => WcxApi.E_BAD_DATA,
        ReassemblyException => WcxApi.E_BAD_DATA,
        ArgumentException => WcxApi.E_ECREATE,
        UnauthorizedAccessException => WcxApi.E_ECREATE,
        IOException => WcxApi.E_EWRITE,
        OutOfMemoryException => WcxApi.E_NO_MEMORY,
        _ => WcxApi.E_BAD_ARCHIVE,
    };

    /// <summary>
    /// Ошибки упаковки и удаления. Источником может быть панель TC: файл успели удалить или закрыть
    /// между показом списка и операцией — это E_EOPEN, а не «архив испорчен».
    /// </summary>
    public static int MapPackError(Exception ex) => ex switch
    {
        PackingAbortedException => WcxApi.E_EABORTED, // пользователь нажал Stop: контейнер не изменён
        NothingToRemoveException => WcxApi.E_NO_FILES,
        FileNotFoundException => WcxApi.E_EOPEN,
        DirectoryNotFoundException => WcxApi.E_EOPEN,
        ContainerFormatException => WcxApi.E_UNKNOWN_FORMAT,
        ChunkIntegrityException => WcxApi.E_BAD_DATA,
        ManifestMismatchException => WcxApi.E_BAD_DATA,
        NotSupportedException => WcxApi.E_NOT_SUPPORTED,
        ArgumentException => WcxApi.E_ECREATE,
        UnauthorizedAccessException => WcxApi.E_ECREATE,
        IOException => WcxApi.E_EWRITE,
        OutOfMemoryException => WcxApi.E_NO_MEMORY,
        _ => WcxApi.E_BAD_ARCHIVE,
    };
}
