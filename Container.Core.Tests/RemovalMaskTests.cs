namespace Container_V3.Tests;

/// <summary>
/// Разбор и сопоставление масок удаления (RemovalPattern).
/// Семантика: «папка\*.*» = папка целиком, «bin\*.log» = файлы папки, «*.txt» = файлы в корне.
/// </summary>
public sealed class RemovalMaskTests
{
    [Fact]
    public void TrailingStarDot_MeansWholeFolder()
    {
        RemovalPattern contents = RemovalPattern.Parse(@"bin\tools\*.*");
        Assert.Equal(RemovalScope.Folder, contents.Scope);
        Assert.Equal(@"bin\tools", contents.Folder);
    }

    [Fact]
    public void MaskWithExtension_IsNotFolderDeletion()
    {
        Assert.Equal(RemovalScope.Mask, RemovalPattern.Parse(@"bin\*.log").Scope);
    }

    [Fact]
    public void PlainName_StaysExact()
    {
        Assert.Equal(RemovalScope.Exact, RemovalPattern.Parse("A.TXT").Scope);
    }

    [Fact]
    public void RootMask_CoversWholeContainer()
    {
        RemovalPattern root = RemovalPattern.Parse(@"*.*");
        Assert.Equal(RemovalScope.Folder, root.Scope);
        Assert.Equal("", root.Folder);
    }

    [Fact]
    public void SlashesAreNormalisedToBackslashes()
    {
        Assert.Equal(@"bin\tools\*.log", RemovalPattern.Parse("bin//tools/*.log").Name);
    }

    public static TheoryData<string, string, bool> MatchCases() => new()
    {
        { @"bin\tools\*.*", @"bin\tools", true },
        { @"bin\tools\*.*", @"bin\tools\a\b.txt", true },
        { @"bin\tools\*.*", @"BIN\TOOLS\A.TXT", true },
        { @"bin\tools\*.*", @"bin", false },
        { @"bin\tools\*.*", @"bin\other.txt", false },
        { @"bin\tools\*.*", @"bin\toolsx\a.txt", false },
        { @"bin\tools\*", @"bin\tools\глубоко\файл.bin", true },
        { @"bin\*.*", @"bin", true },
        { @"bin\*.*", @"bin\tools\helper.log", true },
        { @"bin\*.*", @"other\bin\x.txt", false },
        { @"*.*", @"глубокая\ветка\файл.txt", true },
        { @"bin\*.log", @"bin\notes.log", true },
        { @"bin\*.log", @"bin\sub\notes.log", false },
        { @"bin\*.log", @"binx\notes.log", false },
        { @"bin\*.log", @"bin\notes.logx", false },
        { @"bin\app.tx?", @"bin\app.txt", true },
        { @"bin\app.tx?", @"bin\app.tx", false },
        { @"bin\app.tx?", @"bin\app.txtx", false },
        { @"*.txt", @"отчёт.txt", true },
        { @"*.txt", @"bin\app.txt", false },
        { @"папка юникод", @"папка юникод", true },
        { @"папка юникод", @"Папка ЮНИКОД\внутри.txt", true },
        { @"папка юникод", @"папка юникода.txt", false },
    };

    [Theory]
    [MemberData(nameof(MatchCases))]
    public void MaskMatching(string mask, string path, bool expected)
    {
        Assert.Equal(expected, RemovalPattern.Parse(mask).Matches(path));
    }

    [Theory]
    [InlineData(@"..\вне\*.*")]
    [InlineData(@"bin\..\x")]
    [InlineData(@"C:\bin\*.*")]
    [InlineData(@"bin<>tools\*")]
    [InlineData("bin|tools\\*")]
    [InlineData("   ")]
    [InlineData("")]
    public void DangerousMasks_AreRejected(string mask)
    {
        Assert.Throws<ContainerFormatException>(() => RemovalPattern.Parse(mask));
    }
}
