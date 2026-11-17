using Container_V3.Packing;

namespace Container_V3.CliTests;

/// <summary>
/// Разбор аргументов командной строки: значение размера чанка по умолчанию,
/// запись размера в разных формах (256K / 1M / 4096) и отказ на значениях вне границ формата.
///
/// Это перенос последней части selftest, которая не относится к Container.Core: здесь
/// проверяется пользовательский ввод, а не упаковка. Разделение ответственности намеренное —
/// CLI отвечает за то, чтобы смысл флага не потерялся и не «уре́зался» молча; границы значений
/// знает PackingOptions.Validate. Поэтому часть тестов проверяет именно связку «CLI принял →
/// упаковка отклонила».
///
/// Тесты с пометкой «текущее поведение» фиксируют то, как CLI работает сейчас, а не то, как
/// правильно: если решение менять, такой тест должен упасть первым и напомнить об этом.
/// </summary>
public class CliParseTests
{
    private const int Min = ContainerFormat.MinChunkSize;
    private const int Max = ContainerFormat.MaxChunkSize;
    private const int Default = ContainerFormat.DefaultChunkSize;

    private static Cli.Options Pack(params string[] extra) =>
        Cli.Parse(["pack", "src", "-o", "x.cc", .. extra]);

    // 1. Значения по умолчанию: пользователь молчит про размер — должно быть то же число,
    //    что знает ядро, а не своё (иначе «по умолчанию» разъезжается по сборкам).
    [Fact]
    public void Parse_NoArgs_GivesDefaults()
    {
        Cli.Options o = Cli.Parse([]);

        Assert.Equal("", o.Command);
        Assert.Empty(o.Positional);
        Assert.Null(o.OutputPath);
        Assert.Equal(Default, o.ChunkSize);
        Assert.Equal(ContainerFormat.DefaultCompressionLevel, o.CompressionLevel);
    }

    [Fact]
    public void Parse_WithoutChunkUsesFormatDefault()
    {
        Assert.Equal(Default, Pack([]).ChunkSize);
    }

    [Fact]
    public void DefaultChunkSize_IsTheSameNumberTheCorePacksWith()
    {
        Assert.Equal(new PackingOptions().ChunkSize, Cli.Parse(["pack", "src"]).ChunkSize);
    }

    [Fact]
    public void DefaultCompressionLevel_ComesFromContainerFormat()
    {
        Assert.Equal(ContainerFormat.DefaultCompressionLevel, Cli.DefaultCompressionLevel);
        Assert.Equal(ContainerFormat.DefaultCompressionLevel, Pack([]).CompressionLevel);
    }

    [Fact]
    public void Parse_LowercasesCommandOnly()
    {
        Cli.Options o = Cli.Parse(["PACK", "Имя_Папки", "-o", "X.CC"]);

        Assert.Equal("pack", o.Command);
        // Имена путей регистр сохраняют: иначе распаковка не нашла бы файл на диске.
        Assert.Equal(new[] { "Имя_Папки" }, o.Positional);
        Assert.Equal("X.CC", o.OutputPath);
    }

    // 2. Формы записи размера: K/M с суффиксом, без суффикса, нижний регистр, пробелы,
    //    обе формы флага. Границы диапазона включительно.
    public static TheoryData<string, int> ChunkSizesAccepted() => new()
    {
        { "65536", Min },                 // нижняя граница, без суффикса
        { "256K", 256 * 1024 },
        { "256k", 256 * 1024 },           // суффикс в нижнем регистре
        { "1024K", 1024 * 1024 },
        { "1M", Default },
        { "1m", Default },
        { "16M", Max },                   // верхняя граница
        { " 256K ", 256 * 1024 },         // значение с пробелами (удобно в .bat/.ps1)
    };

    [Theory]
    [MemberData(nameof(ChunkSizesAccepted))]
    public void Parse_ChunkSizeForms(string value, int expected)
    {
        Assert.Equal(expected, Pack(["-chunk", value]).ChunkSize);
        Assert.Equal(expected, Pack(["--chunk", value]).ChunkSize);
    }

    [Fact]
    public void Parse_ChunkLevelFlags_LongAndShortForms()
    {
        Assert.Equal(9, Pack(["--level", "9"]).CompressionLevel);
        Assert.Equal(9, Pack(["-level", "9"]).CompressionLevel);
        Assert.Equal("out.cc", Pack(["--out", "out.cc"]).OutputPath);
    }

    // 3. Отказ, а не молчаливая подмена: CLI обязан сказать пользователю про размер,
    //    а не урезать его до ближайшего допустимого.
    public static TheoryData<string> ChunkSizesRejected() => new()
    {
        { "1K" },                 // 1024 — слишком мелко
        { "32M" },                // больше MaxChunkSize
        { "65535" },              // MinChunkSize - 1
        { "16777217" },           // MaxChunkSize + 1
        { "0" },
        { "-1" },
    };

    [Theory]
    [MemberData(nameof(ChunkSizesRejected))]
    public void Parse_ChunkSizeOutOfRange(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Pack(["-chunk", value]));

        // Сообщение должно называть и сам размер, и диапазон: «вне границ» без чисел
        // пользователю ничем не помогает.
        Assert.True(ex.Message.Contains("диапазон", StringComparison.Ordinal),
            $"в сообщении нет диапазона: {ex.Message}");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("100KB")]   // суффикс B, не K и не M
    [InlineData("1.5M")]
    [InlineData("")]
    [InlineData("K")]
    public void Parse_ChunkSizeNotAParsableNumber(string value)
    {
        Assert.Throws<ArgumentException>(() => Pack(["-chunk", value]));
    }

    [Fact]
    public void Parse_ChunkSizeMissingValue()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Cli.Parse(["pack", "src", "-o", "x.cc", "-chunk"]));

        Assert.Contains("-chunk", ex.Message);
    }

    // 4. Границы ints: 2048M переполняет умножение до того, как включается проверка диапазона.
    //    Текущее поведение — OverflowException, а не аккурат ArgumentException; CLI-обёртка
    //    ловит его общим catch, но сообщение пользователю будет не про размер чанка.
    [Fact]
    public void Parse_ChunkSizeHugeValue_OverflowsBeforeRangeCheck_CurrentBehavior()
    {
        Assert.Throws<OverflowException>(() => Pack(["-chunk", "2048M"]));
        // Аккуратный отказ по диапазону: 32M ещё в пределах int, и его CLI отвергает как OutOfRange.
        Assert.Throws<ArgumentException>(() => Pack(["-chunk", "32M"]));
    }

    // 5. Уровень: CLI разбирает целое, границу «уровень > 0» знает упаковка.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Parse_LevelNonPositive_IsRejectedByPackingValidation(int level)
    {
        Cli.Options o = Pack(["-level", level.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Assert.Equal(level, o.CompressionLevel);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PackingOptions(o.ChunkSize, o.CompressionLevel).Validate());
    }

    // CLI не переводит кривое число в ArgumentException: это FormatException от int.Parse.
    // Текущее поведение; если решим выдавать «не удалось разобрать уровень», тест упадёт первым.
    [Fact]
    public void Parse_LevelNotANumber_FormatException_CurrentBehavior()
    {
        Assert.Throws<FormatException>(() => Pack(["-level", "turbo"]));
    }

    [Fact]
    public void Parse_LevelMissingValue()
    {
        Assert.Throws<ArgumentException>(() => Cli.Parse(["pack", "src", "-o", "x.cc", "--level"]));
    }

    // 6. Позиционные аргументы и выходной файл: флаги могут стоять в любом месте,
    //    порядок источников обязан сохраниться (pack принимает несколько источников).
    [Fact]
    public void Parse_PositionalOrderPreserved_AroundFlags()
    {
        Cli.Options o = Cli.Parse(["pack", "a", "б", "c", "-o", "out.cc", "d"]);

        Assert.Equal(new[] { "a", "б", "c", "d" }, o.Positional);
        Assert.Equal("out.cc", o.OutputPath);
    }

    [Fact]
    public void Parse_RepeatedOut_LastWins_CurrentBehavior()
    {
        Cli.Options o = Cli.Parse(["pack", "a", "-o", "one.cc", "-o", "two.cc"]);

        Assert.Equal("two.cc", o.OutputPath);
        Assert.Equal(new[] { "a" }, o.Positional);
    }

    // Флаги разбираются с точным регистром (команда — нет). Текущее поведение:
    // «-CHUNK» уезжает в позиционные аргументы, и размер молча остаётся default.
    [Fact]
    public void Parse_FlagNamesAreCaseSensitive_CurrentBehavior()
    {
        Cli.Options o = Cli.Parse(["pack", "src", "-CHUNK", "1M"]);

        Assert.Equal(Default, o.ChunkSize);
        Assert.Equal(new[] { "src", "-CHUNK", "1M" }, o.Positional);
    }

    // Значение флага не проверяется на «похожесть на флаг»: «-o -chunk» означает файл named «-chunk».
    // Опечатка в командной строке даст странный, но предсказуемый разбор.
    [Fact]
    public void Parse_FlagAsValueOfAnotherFlag_IsTakenLiterally_CurrentBehavior()
    {
        Cli.Options o = Cli.Parse(["pack", "src", "-o", "-chunk", "1M"]);

        Assert.Equal("-chunk", o.OutputPath);
        Assert.Equal(Default, o.ChunkSize);
        Assert.Equal(new[] { "src", "1M" }, o.Positional);
    }
}
