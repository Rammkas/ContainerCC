using System.Runtime.InteropServices;

namespace WcxHarness;

/// <summary>
/// Загрузка плагина так же, как это делает Total Commander: LoadLibrary + GetProcAddress,
/// дальше вызовы по cdecl. Никакого маршалинга DllImport: харнесс должен видеть ровно те
/// границы, что видит TC.
/// </summary>
internal sealed unsafe class Plugin
{
    public nint Module { get; }
    public string PluginFile { get; }

    public delegate* unmanaged[Cdecl]<tOpenArchiveDataW*, nint> OpenArchiveW;
    public delegate* unmanaged[Cdecl]<nint, tHeaderDataExW*, int> ReadHeaderExW;
    public delegate* unmanaged[Cdecl]<nint, int, char*, char*, int> ProcessFileW;
    public delegate* unmanaged[Cdecl]<nint, int> CloseArchive;
    public delegate* unmanaged[Cdecl]<nint, nint, int> SetProcessDataProcW;
    public delegate* unmanaged[Cdecl]<nint, nint, int> SetChangeVolProcW;
    public delegate* unmanaged[Cdecl]<int> GetPackerCaps;
    public delegate* unmanaged[Cdecl]<int> GetBackgroundFlags;
    public bool HasBackgroundFlags { get; private set; }

    // Экспорты «первого издания» (ANSI-ветка wcxhead.h). Ищем их отдельно и не считаем отсутствующих
    // ошибкой загрузки: TC при отсутствии W-функций зовёт именно эти имена.
    public delegate* unmanaged[Cdecl]<tOpenArchiveData*, nint> OpenArchive;
    public delegate* unmanaged[Cdecl]<nint, tHeaderData*, int> ReadHeader;
    public delegate* unmanaged[Cdecl]<nint, tHeaderDataEx*, int> ReadHeaderEx;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, int> ProcessFile;
    public delegate* unmanaged[Cdecl]<nint, nint, int> SetProcessDataProc;
    public delegate* unmanaged[Cdecl]<nint, nint, int> SetChangeVolProc;

    /// <summary>Плагин отдаёт полный ANSI-цикл (OpenArchive/ReadHeader/ReadHeaderEx/ProcessFile/колбэк).</summary>
    public bool HasAnsiBranch { get; private set; }

    /// <summary>Имена ANSI-экспортов, которые не найдены (харнесс честно сообщает о пропуске).</summary>
    public List<string> MissingAnsiExports { get; } = new();

    // Упаковка и удаление. Их отсутствие не считается ошибкой загрузки: плагин «только распаковка»
    // легален (так себя объявляет cpio.wcx64 этой машины), но тогда харнесс пропускает соответствующие
    // проходы и говорит об этом прямо.
    public delegate* unmanaged[Cdecl]<char*, char*, char*, char*, int, int> PackFilesW;
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, int, int> PackFiles;
    public delegate* unmanaged[Cdecl]<char*, char*, int> DeleteFilesW;
    public delegate* unmanaged[Cdecl]<byte*, byte*, int> DeleteFiles;
    public delegate* unmanaged[Cdecl]<tPackDefaultParamStruct*, void> PackSetDefaultParams;

    public bool HasPackW { get; private set; }
    public bool HasDeleteW { get; private set; }
    public bool HasPackAnsi { get; private set; }
    public bool HasDeleteAnsi { get; private set; }
    public bool HasPackDefaults { get; private set; }

    /// <summary>ConfigurePacker: кнопка «Настроить» в окне TC «Упаковать файлы…», и экспорт с этим именем.</summary>
    public delegate* unmanaged[Cdecl]<nint, nint, void> ConfigurePacker;

    /// <summary>Экспорт ConfigurePacker найден: у плагина есть диалог настроек пакера.</summary>
    public bool HasConfigure { get; private set; }

    public List<string> MissingPackExports { get; } = new();

    private Plugin(nint module, string path)
    {
        Module = module;
        PluginFile = path;
    }

    public static Plugin Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Плагин не найден: {path}");

        nint module = NativeLibrary.Load(Path.GetFullPath(path));
        Plugin plugin = new(module, Path.GetFullPath(path));

        plugin.OpenArchiveW = (delegate* unmanaged[Cdecl]<tOpenArchiveDataW*, nint>)Required(plugin.Module, path, "OpenArchiveW");
        plugin.ReadHeaderExW = (delegate* unmanaged[Cdecl]<nint, tHeaderDataExW*, int>)Required(plugin.Module, path, "ReadHeaderExW");
        plugin.ProcessFileW = (delegate* unmanaged[Cdecl]<nint, int, char*, char*, int>)Required(plugin.Module, path, "ProcessFileW");
        plugin.CloseArchive = (delegate* unmanaged[Cdecl]<nint, int>)Required(plugin.Module, path, "CloseArchive");
        plugin.SetProcessDataProcW = (delegate* unmanaged[Cdecl]<nint, nint, int>)Required(plugin.Module, path, "SetProcessDataProcW");
        plugin.SetChangeVolProcW = (delegate* unmanaged[Cdecl]<nint, nint, int>)Required(plugin.Module, path, "SetChangeVolProcW");
        plugin.GetPackerCaps = (delegate* unmanaged[Cdecl]<int>)Required(plugin.Module, path, "GetPackerCaps");

        if (NativeLibrary.TryGetExport(plugin.Module, "GetBackgroundFlags", out nint background))
        {
            plugin.GetBackgroundFlags = (delegate* unmanaged[Cdecl]<int>)(void*)background.ToPointer();
            plugin.HasBackgroundFlags = true;
        }

        // ANSI-ветка: каждое имя ищем отдельно. Не найденное — не падение харнесса, а честный пропуск
        // проверок «первого издания» (они печатаются в отчёте).
        plugin.HasAnsiBranch = true;
        plugin.HasAnsiBranch &= TryOptional(plugin, "OpenArchive", out nint ansiOpen);
        plugin.OpenArchive = (delegate* unmanaged[Cdecl]<tOpenArchiveData*, nint>)(void*)ansiOpen.ToPointer();
        plugin.HasAnsiBranch &= TryOptional(plugin, "ReadHeader", out nint ansiHeader);
        plugin.ReadHeader = (delegate* unmanaged[Cdecl]<nint, tHeaderData*, int>)(void*)ansiHeader.ToPointer();
        plugin.HasAnsiBranch &= TryOptional(plugin, "ReadHeaderEx", out nint ansiHeaderEx);
        plugin.ReadHeaderEx = (delegate* unmanaged[Cdecl]<nint, tHeaderDataEx*, int>)(void*)ansiHeaderEx.ToPointer();
        plugin.HasAnsiBranch &= TryOptional(plugin, "ProcessFile", out nint ansiProcess);
        plugin.ProcessFile = (delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, int>)(void*)ansiProcess.ToPointer();
        plugin.HasAnsiBranch &= TryOptional(plugin, "SetProcessDataProc", out nint ansiProgress);
        plugin.SetProcessDataProc = (delegate* unmanaged[Cdecl]<nint, nint, int>)(void*)ansiProgress.ToPointer();
        plugin.HasAnsiBranch &= TryOptional(plugin, "SetChangeVolProc", out nint ansiVolume);
        plugin.SetChangeVolProc = (delegate* unmanaged[Cdecl]<nint, nint, int>)(void*)ansiVolume.ToPointer();

        // Упаковка и удаление: W-имена и ANSI-имена ищутся раздельно, как это делает 64-битный TC
        // (сначала W, при отсутствии ANSI).
        plugin.HasPackW = TryPack(plugin, "PackFilesW", out nint packFilesW);
        plugin.PackFilesW = (delegate* unmanaged[Cdecl]<char*, char*, char*, char*, int, int>)(void*)packFilesW.ToPointer();
        plugin.HasDeleteW = TryPack(plugin, "DeleteFilesW", out nint deleteFilesW);
        plugin.DeleteFilesW = (delegate* unmanaged[Cdecl]<char*, char*, int>)(void*)deleteFilesW.ToPointer();
        plugin.HasPackAnsi = TryPack(plugin, "PackFiles", out nint packFilesAnsi);
        plugin.PackFiles = (delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, int, int>)(void*)packFilesAnsi.ToPointer();
        plugin.HasDeleteAnsi = TryPack(plugin, "DeleteFiles", out nint deleteFilesAnsi);
        plugin.DeleteFiles = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)(void*)deleteFilesAnsi.ToPointer();

        // PackSetDefaultParams не обязателен (SDK 2.1+), поэтому его отсутствие в MissingPackExports не пишется.
        plugin.HasPackDefaults = NativeLibrary.TryGetExport(plugin.Module, "PackSetDefaultParams", out nint packDefaults);
        plugin.PackSetDefaultParams = plugin.HasPackDefaults
            ? (delegate* unmanaged[Cdecl]<tPackDefaultParamStruct*, void>)(void*)packDefaults.ToPointer()
            : null;

        // ConfigurePacker ищется отдельно: он нужен только если плагин заявил PK_CAPS_OPTIONS,
        // а его отсутствие при заявленном флаге проверяет Program.cs.
        plugin.HasConfigure = NativeLibrary.TryGetExport(plugin.Module, "ConfigurePacker", out nint configure);
        plugin.ConfigurePacker = plugin.HasConfigure
            ? (delegate* unmanaged[Cdecl]<nint, nint, void>)(void*)configure.ToPointer()
            : null;

        return plugin;
    }

    private static bool TryPack(Plugin plugin, string name, out nint address)
    {
        if (NativeLibrary.TryGetExport(plugin.Module, name, out address))
            return true;

        plugin.MissingPackExports.Add(name);
        address = 0;
        return false;
    }

    private static bool TryOptional(Plugin plugin, string name, out nint address)
    {
        if (NativeLibrary.TryGetExport(plugin.Module, name, out address))
            return true;

        plugin.MissingAnsiExports.Add(name);
        address = 0;
        return false;
    }

    private static void* Required(nint module, string pluginPath, string name)
    {
        if (!NativeLibrary.TryGetExport(module, name, out nint address))
            throw new InvalidOperationException($"«{Path.GetFileName(pluginPath)}» не экспортирует {name} — плагин не годится для TC");

        return address.ToPointer();
    }
}
