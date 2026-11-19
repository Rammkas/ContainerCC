using System.Diagnostics;
using System.Text;
using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Packing;

namespace Container_V3.WcxTests;

/// <summary>
/// Лёгкая часть фикстуара: собирает харнесс и synthetic-дерево с контейнером через Container.Core.
/// Плагина здесь нет намеренно: проверки раскладки и разбора аргументов должны работать в CI,
/// где нельзя собрать NativeAOT (нет MSVC). Тяжёлые тесты используют WcxHarnessFixture.
/// </summary>
public class WcxLayoutFixture : IDisposable
{
    private readonly string _root;

    public const string LayoutSection = "раскладка структур wcxhead.h для x64";
    public const string AnsiLayoutSection = "раскладка ANSI-структур «первого издания» (wcxhead.h) для x64";
    public const string SettingsSectionPrefix = "проход 14.1";

    public WcxLayoutFixture()
    {
        RepoRoot = FindRepoRoot();
        HarnessProjectDir = Path.Combine(RepoRoot, "tools", "wcx-harness");
        HarnessExe = Path.Combine(HarnessProjectDir, "bin", "Release", "net10.0-windows", "win-x64", "wcx-harness.exe");
        PluginPath = Path.Combine(RepoRoot, "publish", "wcx-plugin", "ContainerCC.dll");

        _root = Path.Combine(Path.GetTempPath(), "container_v3_tests", "wcx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        SourceDir = Path.Combine(_root, "src");
        ContainerPath = Path.Combine(_root, "test.cc");

        BuildHarness();
        BuildSourceTree();
        PackContainer();
    }

    public string RepoRoot { get; }
    public string HarnessProjectDir { get; }
    public string HarnessExe { get; private set; }
    public string PluginPath { get; }
    public string SourceDir { get; }
    public string ContainerPath { get; }

    /// <summary>Папка фикстуара: тесты пишут сюда свои черновые файлы — фикстуар их удалит.</summary>
    public string ScratchRoot => _root;

    /// <summary>Запрашивает ли окружение интерактивный проход 14.1 (диалог «Настроить»).</summary>
    public static bool GuiRequested =>
        string.Equals(Environment.GetEnvironmentVariable("WCX_GUI"), "1", StringComparison.Ordinal);

    /// <summary>Прогон раскладки (--layout-only): плагин, контейнер и аргументы прохода не нужны.</summary>
    public HarnessRun LayoutOnlyRun() =>
        Run(writeResults: true, pluginArgs: false, requireResults: true, "--layout-only");

    /// <summary>Полный проход харнесса: реальные --plugin/--container/--source/--workdir + --results.</summary>
    public HarnessRun RunHarness(params string[] extraArgs) =>
        Run(writeResults: true, pluginArgs: true, requireResults: true, extraArgs);

    /// <summary>
    /// Прогон с аргументами как их дал вызывающий тест: без рабочих путей и без --results.
    /// Нужен проверкам разбора командной строки — они намеренно подают битые аргументы.
    /// </summary>
    public HarnessRun RunHarnessRaw(params string[] extraArgs) =>
        Run(writeResults: false, pluginArgs: false, requireResults: true, extraArgs);

    /// <summary>
    /// Прогон, который харнесс обязан отвергнуть на этапе аргументов: итоговый файл при этом
    /// не пишется (код возврата 2 — до проверок дело не доходит), поэтому его отсутствие не ошибка.
    /// </summary>
    public HarnessRun RunHarnessRejected(params string[] extraArgs) =>
        Run(writeResults: false, pluginArgs: false, requireResults: false, extraArgs);

    private HarnessRun Run(bool writeResults, bool pluginArgs, bool requireResults, params string[] extraArgs)
    {
        string workDir = Path.Combine(_root, "work-" + Guid.NewGuid().ToString("N"));

        // Путь --results: либо его дал вызывающий тест (тогда пишем ровно туда и читаем оттуда же),
        // либо создаём свой — иначе тест не узнал бы, какой файл харнесс реально записал.
        int provided = Array.IndexOf(extraArgs, "--results");
        string? results = provided >= 0
            ? extraArgs[provided + 1]
            : writeResults ? Path.Combine(_root, "results-" + Guid.NewGuid().ToString("N") + ".tsv") : null;

        List<string> args = [];
        if (pluginArgs)
        {
            args.AddRange(["--plugin", PluginPath, "--container", ContainerPath,
                           "--source", SourceDir, "--workdir", workDir]);
        }

        args.AddRange(extraArgs);

        if (results is not null && provided < 0)
        {
            args.Add("--results");
            args.Add(results);
        }

        ProcessResult process = RunProcess(HarnessExe, args, RepoRoot, TimeSpan.FromMinutes(25));

        if (results is null)
            return new HarnessRun(process.ExitCode, process.StdOut, process.StdErr, checks: [], resultsPath: null);

        if (!File.Exists(results))
        {
            if (!requireResults)
                return new HarnessRun(process.ExitCode, process.StdOut, process.StdErr, checks: [], results);

            throw new InvalidOperationException(
                $"харнесс не записал --results {results} (exit {process.ExitCode}):\n{process.StdOut}\n{process.StdErr}");
        }

        return new HarnessRun(process.ExitCode, process.StdOut, process.StdErr, ReadResults(results, process), results);
    }

    // ─────────────────────────── артефакты ───────────────────────────

    // Оба фикстуара (лёгкий и тяжёлый) собирают один и тот же проект харнесса, а коллекции
    // xUnit гоняются параллельно: без замка два dotnet build цеплялись бы за один obj.
    private static readonly object HarnessBuildLock = new();

    protected void BuildHarness()
    {
        lock (HarnessBuildLock)
        {
            // -p:Platform наследуется от тестового проекта: `dotnet test -p:Platform=x64` иначе
            // заставил бы харнесс собраться в другую папку bin\<Configuration> и фикстуар не нашёл бы exe.
            // MSBuild отдаёт Platform и тестовому хосту через переменную окружения.
            List<string> arguments =
            [
                "build", Path.Combine(HarnessProjectDir, "wcx-harness.csproj"),
                "-c", "Release", "--nologo", "-v", "q",
            ];
            string? platform = PlatformToForward();
            if (platform is not null)
                arguments.Add("-p:Platform=" + platform);

            ProcessResult result = RunProcess(DotnetPath, arguments, RepoRoot, TimeSpan.FromMinutes(10));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"не собрался харнесс (exit {result.ExitCode}):\n{result.StdOut}\n{result.StdErr}");
            }

            // Путь к exe собираем не по памяти, а ищем в bin после сборки: TFM и RID в csproj
            // меняются чаще, чем это замечает жёстко зашитая строка.
            if (!File.Exists(HarnessExe))
                HarnessExe = FindHarnessExe() ?? HarnessExe;

            if (!File.Exists(HarnessExe))
            {
                throw new InvalidOperationException(
                    $"сборка прошла, но не найден {HarnessExe}. Содержимое {Path.Combine(HarnessProjectDir, "bin")}:\n"
                    + ListHarnessBin() + "\n" + result.StdOut + "\n" + result.StdErr);
            }
        }
    }

    private string? FindHarnessExe()
    {
        string binRoot = Path.Combine(HarnessProjectDir, "bin");
        if (!Directory.Exists(binRoot))
            return null;

        foreach (string candidate in Directory.EnumerateFiles(binRoot, "wcx-harness.exe", SearchOption.AllDirectories))
        {
            if (candidate.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Платформу тестового проекта (`dotnet test -p:Platform=x64`) MSBuild передаёт тестовому хосту
    /// через переменную окружения Platform. Вложенные build/publish обязаны получить её иначе
    /// артефакты попадут в bin\&lt;другая платформа&gt;\ и фикстуар их не найдёт.
    /// </summary>
    protected static string? PlatformToForward()
    {
        string? platform = Environment.GetEnvironmentVariable("WCX_HARNESS_PLATFORM")
            ?? Environment.GetEnvironmentVariable("Platform");

        return string.IsNullOrWhiteSpace(platform) ? null : platform.Trim();
    }

    private string ListHarnessBin()
    {
        try
        {
            string binRoot = Path.Combine(HarnessProjectDir, "bin");
            if (!Directory.Exists(binRoot))
                return "папка bin отсутствует";

            var lines = new List<string>();
            foreach (string file in Directory.EnumerateFiles(binRoot, "*", SearchOption.AllDirectories))
                lines.Add(file.Substring(binRoot.Length));
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return lines.Count == 0 ? "папка bin пуста" : string.Join("\n", lines.Take(60));
        }
        catch (Exception ex)
        {
            return $"не удалось перечислить bin: {ex.Message}";
        }
    }

    /// <summary>
    /// То же дерево, что генерировал .harness\make_src.py: файл, делящий чанк, пустые файлы и
    /// папки, псевдослучайные несжимаемые данные, кириллические имена (ANSI-проходам нужны имена,
    /// представимые в code page 1251), длинное вложенное имя и имя с пробелом.
    /// </summary>
    protected void BuildSourceTree()
    {
        var random = new Random(20261117);

        WriteText("readme.txt", "Container_V3 harness fixture\n", 420);
        WriteText(Path.Combine("notes", "план-конспект.txt"), "Container_V3 harness fixture\n", 2048);
        WriteText(Path.Combine("notes", "подпапка", "заметка.md"), "Container_V3 harness fixture\n", 900);

        WriteRandom(Path.Combine("data", "big.bin"), 3_000_000, random);
        WriteRandom(Path.Combine("data", "medium.bin"), 131_072, random);
        WriteRandom(Path.Combine("data", "empty.bin"), 0, random);
        WriteRandom(Path.Combine("data", "nested", "a", "b", "c", "глубокий", "файл.bin"), 4096, random);
        WriteRandom(Path.Combine("data", "диск", "файл с пробелами.bin"), 5000, random);

        WriteRandom(Path.Combine("images", "photo-01.png"), 60_000, random);
        WriteRandom(Path.Combine("images", "photo-02.png"), 61_000, random);

        WriteRandom(Path.Combine("dir-" + new string('a', 40), "sub-" + new string('b', 40), "deep-name.bin"), 1024, random);

        Directory.CreateDirectory(Path.Combine(SourceDir, "empty-dir"));
        Directory.CreateDirectory(Path.Combine(SourceDir, "empty-dir2", "inner"));
    }

    private void WriteText(string relative, string line, int size)
    {
        string path = Path.Combine(SourceDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        for (int written = 0; written < size;)
        {
            int take = Math.Min(bytes.Length, size - written);
            stream.Write(bytes, 0, take);
            written += take;
        }
    }

    private void WriteRandom(string relative, int size, Random random)
    {
        string path = Path.Combine(SourceDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] buffer = new byte[Math.Min(size, 64 * 1024)];
        using var stream = File.Create(path);
        for (int written = 0; written < size;)
        {
            int take = Math.Min(buffer.Length, size - written);
            random.NextBytes(buffer.AsSpan(0, take));
            stream.Write(buffer, 0, take);
            written += take;
        }
    }

    protected void PackContainer()
    {
        // Те же параметры упаковки, что у CLI и selftest: чанк 1 MiB, уровень 3.
        using var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(),
            new PackingOptions(1 << 20, 3), () => new ZstdCompressor(3));

        packer.Pack(ContainerPath, [SourceDir]);
    }

    // ─────────────────────────── процесс и разбор TSV ───────────────────────────

    protected static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout)
    {
        ProcessStartInfo start = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Кириллица в stdout/stderr дочернего процесса: без явной кодировки её кодируют
            // кодовой страницей консоли (на CI-раннере — 437, где кириллица становится '?').
            // Харнесс пишет UTF-8 (Console.OutputEncoding в Program.cs), здесь та же кодировка
            // задаётся читающей стороне.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;

        using Process process = new() { StartInfo = start };
        process.Start();

        // Оба потока читаем параллельно: переполнится буфер канала — процесс зависнет на записи.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            throw new TimeoutException($"'{Path.GetFileName(fileName)}' не завершился за {timeout.TotalMinutes} мин");
        }

        // После выхода процесса потоки уже вычитаны; ждём их только на случай асинхронного конца.
        string stdout = stdoutTask.Wait(TimeSpan.FromSeconds(30)) ? stdoutTask.Result : string.Empty;
        string stderr = stderrTask.Wait(TimeSpan.FromSeconds(30)) ? stderrTask.Result : string.Empty;

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static IReadOnlyList<HarnessCheck> ReadResults(string path, ProcessResult process)
    {
        List<HarnessCheck> checks = [];
        bool firstLine = true;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;

            string[] cells = line.Split('\t');

            // Заголовок «section<TAB>result<TAB>what» по форме — такая же тройка, как строка
            // проверки: его отсекаем по имени, иначе он поселился бы в итоге как провал.
            if (firstLine)
            {
                firstLine = false;
                continue;
            }

            if (cells.Length < 3)
                continue;

            checks.Add(new HarnessCheck(cells[0], cells[1], cells[2]));
        }

        if (checks.Count == 0)
        {
            throw new InvalidOperationException(
                $"в {path} нет ни одной проверки — харнесс не отработал (exit {process.ExitCode}):\n{process.StdOut}");
        }

        return checks;
    }

    private static string FindRepoRoot()
    {
        string? assemblyDirectory = Path.GetDirectoryName(typeof(WcxLayoutFixture).Assembly.Location);
        DirectoryInfo? directory = assemblyDirectory is null ? null : new DirectoryInfo(assemblyDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Container_V3.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("не нашёл Container_V3.sln над папкой тестовой сборки");
    }

    protected static string DotnetPath
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
                return fromEnvironment;

            return "dotnet";
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка в Path.GetTempPath(): пусть её уберёт уборщик временных файлов.
        }
    }
}

/// <summary>
/// Тяжёлая часть фикстуара: NativeAOT-плагин и один кэшированный полный прогон харнесса.
/// Прогон дорогой (сотни обращений к плагину), поэтому тесты сверяют разные секции одного TSV,
/// а не гоняют харнесс заново.
///
/// Плагин собирается, если его нет или задан WCX_REBUILD=1. Отдельно тулчейн не выпытывается:
/// NativeAOT может обойтись без cl.exe в PATH, поэтому решение о сборке отдаёт сам publish,
/// а его провал объясняет подсказку про фильтр:
///   dotnet test Container_V3.sln --filter "Category!=WcxNative"
/// Ключ именно Category: адаптер VSTest кладёт Trait("Category", ...) в свойство Category,
/// а TestCategory к трейтам xUnit не применяется (проверено на xunit 2.9.3).
/// WCX_BUILD_PLUGIN=0 запрещает сборку (используется только готовый publish\wcx-plugin).
/// </summary>
public sealed class WcxHarnessFixture : WcxLayoutFixture
{
    private static readonly object BuildLock = new();
    private HarnessRun? _fullRun;

    /// <summary>Секции полного прохода (без раскладок): каждая — отдельный проход харнесса.</summary>
    public static readonly IReadOnlyList<string> PassSections =
    [
        "проход 1: список (PK_OM_LIST) — то, что покажет панель TC",
        "сверка списка контейнера с оригиналом на диске",
        "проход 2: извлечение (PK_OM_EXTRACT) — копирование файлов ИЗ контейнера",
        "проход 3: проверка (PK_TEST) — целостность без записи на диск",
        "проход 4: пользователь нажал Stop (tProcessDataProcW возвращает 0)",
        "проход 5: не контейнер и отсутствующий файл",
        "проход 6: чужой handle, повторный CloseArchive, ProcessFile без ReadHeaderEx",
        "проход 7: ANSI-список (PK_OM_LIST) — OpenArchive/ReadHeaderEx без суффикса W",
        "проход 8: фактические смещения ANSI-структур (sentinel 0xAA + гард-зона 64 байта)",
        "проход 9: ANSI ReadHeader (tHeaderData, «первое издание», PackSize/UnpSize по 32 бита)",
        "ANSI против Unicode: построчная сверка листингов одного контейнера",
        "ANSI-сверка списка контейнера с оригиналом на диске",
        "проход 10: ANSI-извлечение (PK_OM_EXTRACT) — ProcessFile без суффикса W, ANSI-пути и ANSI-колбэк",
        "проход 11: ANSI PK_TEST (проверка «Проверить» в Total Commander)",
        "проход 12: остановка из ANSI-колбэка (tProcessDataProc возвращает 0 → E_EABORTED)",
        "проход 13: ANSI-ветка на битых входах, пустом имени и чужом handle",
        "проход 14: упаковка (PackFilesW) и удаление (DeleteFilesW) — контейнер как обычная папка",
        "проход 15: ANSI-упаковка (PackFiles/DeleteFiles) и чтение юникод-веткой",
    ];

    public WcxHarnessFixture()
    {
        EnsurePlugin();
    }

    /// <summary>Полный проход харнесса с --skip-settings: один на весь запуск тестов.</summary>
    public HarnessRun FullRun
    {
        get
        {
            if (_fullRun is null)
                _fullRun = RunHarness("--skip-settings");

            return _fullRun;
        }
    }

    /// <summary>Полный проход вместе с диалогом «Настроить» (только при WCX_GUI=1).</summary>
    public HarnessRun FullRunWithSettings() => RunHarness();

    private void EnsurePlugin()
    {
        bool rebuild = Environment.GetEnvironmentVariable("WCX_REBUILD") == "1" || !File.Exists(PluginPath);
        if (!rebuild)
            return;

        if (Environment.GetEnvironmentVariable("WCX_BUILD_PLUGIN") == "0")
        {
            if (!File.Exists(PluginPath))
                throw new InvalidOperationException($"нет плагина {PluginPath}, а WCX_BUILD_PLUGIN=0");

            return;
        }

        lock (BuildLock)
        {
            List<string> arguments =
            [
                "publish", Path.Combine(RepoRoot, "ContainerWcx", "ContainerWcx.csproj"),
                "-c", "Release", "-r", "win-x64", "--nologo", "-v", "q",
            ];
            string? platform = PlatformToForward();
            if (platform is not null)
                arguments.Add("-p:Platform=" + platform);

            ProcessResult result = RunProcess(DotnetPath, arguments, RepoRoot, TimeSpan.FromMinutes(25));

            if (result.ExitCode != 0 || !File.Exists(PluginPath))
            {
                throw new InvalidOperationException(
                    $"не собрался NativeAOT-плагин (exit {result.ExitCode}). Если в этой среде нет AOT-тулчейна, "
                    + "эти тесты отсеивают фильтром: dotnet test --filter \"Category!=WcxNative\"\n"
                    + $"{result.StdOut}\n{result.StdErr}");
            }
        }
    }

}
