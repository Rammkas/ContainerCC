namespace Container_V3.WcxTests;

/// <summary>
/// Основной прогон харнесса по плагину: каждая секция TSV — отдельный проход WCX-экспортов
/// (список, извлечение, проверка, остановка, битые входы, ANSI-ветки, упаковка и удаление).
/// Тесты сверяют не текст консоли, а machine-readable итог: в секции ноль провалов, секция
/// отработала, проваленных проверок «без секции» нет.
///
/// Трейт WcxNative: тестам нужен собранный NativeAOT-плагин (нужен MSVC). На CI без Visual
/// Studio их отфильтровывают: dotnet test --filter "Category!=WcxNative"
/// (ключ Category, не TestCategory: Trait("Category", ...) ложится в свойство Category).
/// </summary>
[Collection(WcxNativeCollection.Name)]
[Trait("Category", "WcxNative")]
public sealed class HarnessSectionTests(WcxHarnessFixture fixture)
{
    private readonly WcxHarnessFixture _fixture = fixture;

    /// <summary>Полный прогон один раз на весь запуск тестов: он дорогой, повторять незачем.</summary>
    private HarnessRun Run => _fixture.FullRun;

    public static TheoryData<string> AllPassSections =>
        new(WcxHarnessFixture.PassSections.ToArray());

    [Fact]
    public void FullRunHasNoFailures()
    {
        HarnessRun run = Run;

        Assert.True(run.ExitCode == 0, $"харнесс вернул exit {run.ExitCode}");
        Assert.True(run.Failures.Count == 0,
            $"провалено {run.Failures.Count} из {run.Total} проверок: " +
            string.Join("; ", run.Failures.Select(f => $"{f.Section} → {f.What}")));
    }

    [Theory]
    [MemberData(nameof(AllPassSections))]
    public void PassSectionRanEveryCheck(string section)
    {
        HarnessRun run = Run;
        int count = run.CountIn(section);

        Assert.True(count > 0, $"секция «{section}» не оставила ни одной проверки");
        Assert.True(run.FailuresIn(section).Count == 0,
            $"в секции «{section}» провалено {run.FailuresIn(section).Count} проверок: " +
            string.Join("; ", run.FailuresIn(section)));
    }

    [Fact]
    public void EveryPassSectionIsPresentInResults()
    {
        HarnessRun run = Run;
        List<string> missing = WcxHarnessFixture.PassSections.Where(s => run.CountIn(s) == 0).ToList();

        Assert.True(missing.Count == 0, "секции пропали из итога: " + string.Join(" | ", missing));
    }

    [Fact]
    public void NoCheckIsAttributedToAnUnknownSection()
    {
        // Неизвестное имя секции значит, что харнесс добавил проход, а тесты о нём не знают:
        // его провал не попал бы ни в один отчёт.
        HashSet<string> known = [.. WcxHarnessFixture.PassSections,
                                 WcxLayoutFixture.LayoutSection, WcxLayoutFixture.AnsiLayoutSection];

        List<string> unknown = Run.Sections.Where(s => !known.Contains(s)).ToList();

        Assert.True(unknown.Count == 0, "секции вне списка тестов: " + string.Join(" | ", unknown));
    }

    [Fact]
    public void FullRunChecksAtLeastTheKnownMinimum()
    {
        // Эталон локального прогона — 431 проверка. Точное число зависит от числа файлов и записей
        // в тестовом дереве, поэтому порог занижен: он ловит пропавший проход, а не колебания циклов.
        HarnessRun run = Run;

        Assert.True(run.Total >= 400, $"прогонов меньше ожидаемого: {run.Total}");
    }

    [Fact]
    public void FullRunOpensLayoutSectionsToo()
    {
        // Полный прогон начинает с раскладок (без них остальное не имеет смысла), поэтому
        // обе раскладочные секции обязаны быть и в нём.
        HarnessRun run = Run;

        Assert.Contains(WcxLayoutFixture.LayoutSection, run.Sections);
        Assert.Contains(WcxLayoutFixture.AnsiLayoutSection, run.Sections);
    }

    [Fact]
    public void PluginReportedItsCapabilities()
    {
        // GetPackerCaps печатается в консоль: если плагин не загрузился, харнесс до этого не дойдёт.
        Assert.Contains("GetPackerCaps", Run.Console);
    }
}
