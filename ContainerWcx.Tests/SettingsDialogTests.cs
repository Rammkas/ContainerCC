namespace Container_V3.WcxTests;

/// <summary>
/// Проход 14.1 — диалог «Настроить» плагина. Харнесс открывает окно плагина через user32
/// (FindWindowW, WM_MOUSEWHEEL, ComboLBox), поэтому ему нужен интерактивный рабочий стол:
/// в CI он выключен флагом --skip-settings, а тесты включают его переменной окружения WCX_GUI=1.
///
/// В xUnit 2.9.3 нет рантаймового пропуска проверок, поэтому gate устроен как Theory с одним
/// рядом (запрошен рабочий стол или нет): обе ветки содержат настоящее утверждение, и тест
/// не превращается в пустой.
/// </summary>
[Collection(WcxNativeCollection.Name)]
[Trait("Category", "WcxNative")]
public sealed class SettingsDialogTests(WcxHarnessFixture fixture)
{
    private readonly WcxHarnessFixture _fixture = fixture;

    public static TheoryData<bool> GuiGate { get; } = new() { WcxLayoutFixture.GuiRequested };

    [Fact]
    public void SkippedSettingsPassLeavesNoChecks()
    {
        // При --skip-settings харнесс печатает секцию и строку ПРОПУСК, но проверок не делает:
        // значит в TSV не должно быть ни одной записи этого прохода. Именно так тест понимает
        // «диалог не открывали», не трогая рабочий стол.
        HarnessRun run = _fixture.FullRun;

        List<HarnessCheck> settings = run.Checks
            .Where(c => c.Section.StartsWith(WcxLayoutFixture.SettingsSectionPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(settings);
        Assert.Contains("--skip-settings", run.Console);
    }

    [Theory]
    [MemberData(nameof(GuiGate))]
    public void SettingsPassRunsOnlyWhenDesktopRequested(bool desktopRequested)
    {
        if (desktopRequested)
        {
            HarnessRun run = _fixture.FullRunWithSettings();
            int checks = run.Checks.Count(c => c.Section.StartsWith(WcxLayoutFixture.SettingsSectionPrefix, StringComparison.Ordinal));

            Assert.True(checks > 0, "WCX_GUI=1, но проход 14.1 не оставил ни одной проверки");
            Assert.True(run.ExitCode == 0, $"харнесс вернул exit {run.ExitCode}: {string.Join("; ", run.Failures.Select(f => f.What))}");
        }
        else
        {
            // Рабочего стола нет — проверка обязана подтвердить, что интерактивного прохода не было:
            // иначе тест молча «проходил» бы на машине без GUI.
            int checks = _fixture.FullRun.Checks
                .Count(c => c.Section.StartsWith(WcxLayoutFixture.SettingsSectionPrefix, StringComparison.Ordinal));

            Assert.Equal(0, checks);
        }
    }

    [Theory]
    [MemberData(nameof(GuiGate))]
    public void SettingsPassHasNoFailuresWhenRun(bool desktopRequested)
    {
        if (!desktopRequested)
        {
            // Вне рабочего стола проход не запускался — провалов у него быть не может по определению.
            Assert.True(_fixture.FullRun.Failures.Count == 0, "провалы вне прохода 14.1: " +
                string.Join("; ", _fixture.FullRun.Failures.Select(f => $"{f.Section} → {f.What}")));
            return;
        }

        HarnessRun run = _fixture.FullRunWithSettings();
        List<string> failures = run.Checks
            .Where(c => !c.Ok && c.Section.StartsWith(WcxLayoutFixture.SettingsSectionPrefix, StringComparison.Ordinal))
            .Select(c => $"{c.Section} → {c.What}")
            .ToList();

        Assert.True(failures.Count == 0, "провалено в проходе 14.1: " + string.Join("; ", failures));
    }
}
