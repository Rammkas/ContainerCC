namespace Container_V3.WcxTests;

/// <summary>Завершённый внешний процесс: код возврата и вычитанные потоки.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Одна строка TSV из --results: секция, OK|FAIL, текст проверки.</summary>
public sealed record HarnessCheck(string Section, string Result, string What)
{
    public bool Ok => Result == "OK";
}

/// <summary>Результат одного запуска харнесса: код возврата, консоль и разобранный TSV.</summary>
public sealed class HarnessRun
{
    public HarnessRun(int exitCode, string console, string stderr, IReadOnlyList<HarnessCheck> checks, string? resultsPath = null)
    {
        ExitCode = exitCode;
        Console = console;
        StdErr = stderr;
        Checks = checks;
        ResultsPath = resultsPath;
    }

    public int ExitCode { get; }
    public string Console { get; }
    public string StdErr { get; }
    public IReadOnlyList<HarnessCheck> Checks { get; }

    /// <summary>Файл --results, из которого разобран итог (null — итог не записывался).</summary>
    public string? ResultsPath { get; }

    public int Total => Checks.Count;
    public IReadOnlyList<HarnessCheck> Failures => Checks.Where(c => !c.Ok).ToList();

    /// <summary>Секции в порядке их появления в прогоде.</summary>
    public IReadOnlyList<string> Sections =>
        Checks.Select(c => c.Section).Distinct().ToList();

    public IEnumerable<HarnessCheck> InSection(string section) =>
        Checks.Where(c => c.Section == section);

    public int CountIn(string section) => Checks.Count(c => c.Section == section);

    /// <summary>Проверки до первой Section(): у харнесса их быть не должно.</summary>
    public IReadOnlyList<HarnessCheck> WithoutSection =>
        Checks.Where(c => c.Section == "без секции").ToList();

    /// <summary>Тексты проваленных проверок данной секции (пусто — значит секция чистая).</summary>
    public IReadOnlyList<string> FailuresIn(string section) =>
        InSection(section).Where(c => !c.Ok).Select(c => c.What).ToList();
}
