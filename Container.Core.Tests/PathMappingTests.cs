using Container_V3.Unpacking;

namespace Container_V3.Tests;

/// <summary>
/// Отображение пути записей (.cc хранит «\») в путь на диске. Проверка нужна до переноса
/// на Android: там обратный слэш — символ имени файла, и «docs\a.txt» без сборки из
/// сегментов стал бы одним файлом вместо вложенной папки.
/// </summary>
public sealed class PathMappingTests : IDisposable
{
    private readonly string _root;
    private readonly string _dest;

    public PathMappingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "container_v3_tests",
            "pathmap-" + Guid.NewGuid().ToString("N"));
        _dest = Path.Combine(_root, "map");
        Directory.CreateDirectory(_dest);
    }

    [Theory]
    [InlineData(@"docs\a.txt")]
    [InlineData("docs/a.txt")]
    public void BothSeparatorVariants_ProduceNestedDiskPath(string variant)
    {
        string expected = Path.Combine(_dest, "docs", "a.txt");
        Assert.Equal(expected, ContainerFileSystem.ToAbsolutePath(_dest, variant));
    }

    [Fact]
    public void DeeplyNestedPath_StaysATree()
    {
        string nested = ContainerFileSystem.ToAbsolutePath(_dest, @"docs\sub\a.txt");
        Assert.Equal(Path.Combine(_dest, "docs", "sub", "a.txt"), nested);
    }

    // Выход за пределы папки распаковки. Прежняя проверка StartsWith пропускала «outside»,
    // когда папка называлась «out»: теперь требуется разделитель сразу после корня.
    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"..\..\outside.txt")]
    [InlineData("C:evil.txt")]
    [InlineData(@"docs\..\..\x.bin")]
    [InlineData(@"\\")]
    public void EscapePaths_AreRejected(string bad)
    {
        string outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);

        Assert.Throws<ContainerFormatException>(
            () => ContainerFileSystem.ToAbsolutePath(outDir, bad));
    }

    // Две записи, ведущие в один файл на диске: без проверки победил бы последний записанный.
    [Fact]
    public void TwoEntriesCollidingOnCaseInsensitiveDisk_AreRejected()
    {
        var chunk = new ChunkInfo(0, 0, 0, 10);
        var dupIndex = new ContainerIndex(
            [
                new FileEntry("dup.TXT", 10, false, false, [new ChunkFragmentRef(0, 0, 10)]),
                new FileEntry("dup.txt", 10, false, false, [new ChunkFragmentRef(0, 0, 10)]),
            ],
            [chunk]);

        Assert.Throws<ContainerFormatException>(() => RestorePlanner.Build(dupIndex, _dest));
    }

    [Fact]
    public void ValidIndex_ProducesOneTargetWithDiskPath()
    {
        var chunk = new ChunkInfo(0, 0, 0, 10);
        var okIndex = new ContainerIndex(
            [new FileEntry(@"docs\a.txt", 10, false, false, [new ChunkFragmentRef(0, 0, 10)])],
            [chunk]);

        var built = RestorePlanner.Build(okIndex, _dest);

        Assert.Single(built);
        Assert.Equal(Path.Combine(_dest, "docs", "a.txt"), built[0].AbsolutePath);
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
        }
    }
}
