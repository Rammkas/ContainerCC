namespace Container_V3.WcxTests;

/// <summary>
/// Разбор аргументов харнесса. Плагина здесь нет намеренно: код возврата 2 — это отказ
/// принять командную строку, до обращений к DLL дело не доходит, значит тесты работают и на CI.
/// Коды возврата харнесса: 2 — плохие аргументы, 1 — проваленная проверка, 0 — всё чисто.
/// </summary>
[Collection(WcxLayoutCollection.Name)]
public sealed class HarnessArgsTests(WcxLayoutFixture fixture)
{
    private readonly WcxLayoutFixture _fixture = fixture;

    [Fact]
    public void MissingRequiredArgumentsExitWithTwo()
    {
        HarnessRun run = _fixture.RunHarnessRejected();

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Нужны --plugin", run.Console);
    }

    [Fact]
    public void UnknownParameterExitsWithTwo()
    {
        HarnessRun run = _fixture.RunHarnessRejected("--layout-only", "--not-a-flag", "value");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Неизвестный параметр --not-a-flag", run.Console);
    }

    [Fact]
    public void UnknownParameterWithoutValueReportsMissingValue_CurrentBehavior()
    {
        // Харнесс не знает, что флаг неизвестен, пока не разберёт его: незнакомый флаг считается
        // параметром со значением, поэтому в конце строки сообщение — про отсутствующее значение.
        HarnessRun run = _fixture.RunHarnessRejected("--not-a-flag");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Не хватает значения после --not-a-flag", run.Console);
    }

    [Fact]
    public void ParameterWithoutValueExitsWithTwo()
    {
        HarnessRun run = _fixture.RunHarnessRejected("--plugin");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Не хватает значения после --plugin", run.Console);
    }

    [Fact]
    public void RejectedArgumentsWriteNoResultsFile()
    {
        // Контракт --results: файл появляется только если харнесс дошёл до итога. При exit 2
        // проверок не было, и тест обязан это выдержать, а не падать на отсутствующем файле.
        string results = Path.Combine(_fixture.ScratchRoot, "rejected.tsv");

        HarnessRun run = _fixture.RunHarnessRejected("--results", results);

        Assert.Equal(2, run.ExitCode);
        Assert.False(File.Exists(results));
        Assert.Empty(run.Checks);
        Assert.Equal(results, run.ResultsPath);
    }

    [Fact]
    public void FlagsWithoutValuesDoNotEatTheNextParameter()
    {
        // Рано флаги без значения (--layout-only, --skip-settings) съедали следующий флаг как своё
        // значение, и порядок аргументов ломался. Сейчас они сдвигают индекс на 1.
        HarnessRun run = _fixture.RunHarnessRaw("--layout-only", "--skip-settings");

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Console}");
        Assert.DoesNotContain("Неизвестный параметр", run.Console);
        Assert.DoesNotContain("Не хватает значения", run.Console);
    }

    [Fact]
    public void LayoutOnlyRunsEvenWithSkipSettingsAndNoPluginArguments()
    {
        HarnessRun run = _fixture.RunHarnessRaw("--skip-settings", "--layout-only");

        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void MissingPluginFileFailsInsteadOfReportingSuccess()
    {
        // Плагина нет — харнесс обязан провалиться, а не отрапортовать «пройден». Проверки
        // раскладки к этому моменту уже отработали, поэтому итог пишется и exit равен 1.
        string results = Path.Combine(_fixture.ScratchRoot, "no-plugin.tsv");

        HarnessRun run = _fixture.RunHarnessRaw(
            "--plugin", Path.Combine(_fixture.ScratchRoot, "no-such-plugin.dll"),
            "--container", _fixture.ContainerPath,
            "--source", _fixture.SourceDir,
            "--workdir", Path.Combine(_fixture.ScratchRoot, "work-no-plugin"),
            "--results", results);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(run.Checks, c => !c.Ok && c.What.Contains("Плагин не найден"));
    }

    [Fact]
    public void FullRunNeedsAllFourWorkingArguments()
    {
        // Тот же набор без --workdir: харнесс не должен «догадываться» о рабочей папке —
        // ANSI-проходы пишут рядом, и угаданная папка означала бы порчу чужих файлов.
        HarnessRun run = _fixture.RunHarnessRejected(
            "--plugin", _fixture.PluginPath,
            "--container", _fixture.ContainerPath,
            "--source", _fixture.SourceDir);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--workdir", run.Console);
    }
}
