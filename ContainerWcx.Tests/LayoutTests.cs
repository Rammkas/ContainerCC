namespace Container_V3.WcxTests;

/// <summary>
/// Проверки раскладки структур wcxhead.h: харнесс сверяет Marshal-раскладки со значениями из
/// официального заголовка Total Commander. Плагин для этого не нужен (--layout-only), поэтому
/// тесты работают где угодно — в CI без Visual Studio, без NativeAOT-тулчейна и без рабочего стола.
/// </summary>
[Collection(WcxLayoutCollection.Name)]
public sealed class LayoutTests(WcxLayoutFixture fixture)
{
    private readonly WcxLayoutFixture _fixture = fixture;

    // Один прогон на весь класс: харнесс дешёвый (плагин не грузится), но повторять его
    // в каждом тесте смысла нет.
    private HarnessRun Run => _fixture.LayoutOnlyRun();

    [Fact]
    public void LayoutOnlyChecksEveryUnicodeStruct()
    {
        HarnessRun run = Run;
        int count = run.CountIn(WcxLayoutFixture.LayoutSection);

        Assert.True(count >= 28, $"проверок раскладки wcxhead.h меньше ожидаемого: {count}");
    }

    [Fact]
    public void LayoutOnlyChecksEveryAnsiStruct()
    {
        HarnessRun run = Run;
        int count = run.CountIn(WcxLayoutFixture.AnsiLayoutSection);

        Assert.True(count >= 48, $"ANSI-раскладок меньше ожидаемого: {count}");
    }

    [Fact]
    public void LayoutOnlyHasNoFailures()
    {
        HarnessRun run = Run;

        Assert.True(run.Failures.Count == 0,
            "проваленные раскладки: " + string.Join("; ", run.Failures.Select(f => f.What)));
        Assert.True(run.ExitCode == 0, $"харнесс вернул exit {run.ExitCode}: {run.Console}");
    }

    [Fact]
    public void LayoutOnlyOpensExactlyTwoSections()
    {
        HarnessRun run = Run;

        Assert.True(run.Sections.Count == 2,
            "ожидались ровно две секции раскладки, получено: " + string.Join(" | ", run.Sections));
        Assert.Contains(WcxLayoutFixture.LayoutSection, run.Sections);
        Assert.Contains(WcxLayoutFixture.AnsiLayoutSection, run.Sections);
    }

    [Fact]
    public void EveryCheckHasItsSection()
    {
        HarnessRun run = Run;

        // Проверка до первой Section() означала бы, что харнесс пишет итог вне секций:
        // тогда проваленную проверку нельзя было бы отнести к проходу.
        Assert.True(run.WithoutSection.Count == 0,
            "проверки без секции: " + string.Join("; ", run.WithoutSection.Select(c => c.What)));
    }

    [Fact]
    public void LayoutOnlyNeedsNoPluginContainerOrWorkDir()
    {
        // --layout-only обходит требование четырёх обязательных аргументов: иначе раскладку
        // нельзя было бы проверить в CI, где нет ни плагина, ни контейнера.
        HarnessRun run = Run;

        Assert.True(run.ExitCode == 0, $"харнесс потребовал аргументы, которых нет при --layout-only (exit {run.ExitCode})");
        Assert.DoesNotContain("Нужны --plugin", run.Console);
    }

    [Fact]
    public void ResultsFileIsUtf8WithoutBom()
    {
        // TSV читаем как UTF-8: BOM попал бы в первое имя секции и рассогласил сверку названий.
        HarnessRun run = Run;
        byte[] head = File.ReadAllBytes(run.ResultsPath!);

        Assert.True(head.Length > 3, "слишком короткий TSV");
        Assert.False(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
            "TSV начинается с BOM — разбор имён секций сломается");
    }

    [Fact]
    public void ResultsFileHasHeaderAndTsvCells()
    {
        string[] lines = File.ReadAllLines(Run.ResultsPath!);

        Assert.Equal("section\tresult\twhat", lines[0]);
        Assert.True(lines.Length > 1, "в TSV нет ни одной строки проверки");

        // Текст проверки не должен содержать табуляцию: иначе колонки съехали бы.
        foreach (string line in lines[1..])
        {
            Assert.Equal(3, line.Split('\t').Length);
        }
    }
}
