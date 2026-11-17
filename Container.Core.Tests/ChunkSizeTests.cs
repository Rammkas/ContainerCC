using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace Container_V3.Tests;

/// <summary>
/// Значение chunk size по умолчанию и границы. Проверяется не содержимое контейнера,
/// а то, какое число попадает в заголовок, и что слишком маленький/большой чанк
/// не принимается, а не молча подменяется.
/// CLI-часть сценария (Cli.Parse) живёт в проекте CLI и переносится отдельно.
/// </summary>
public sealed class ChunkSizeTests
{
    [Fact]
    public void PackingOptions_DefaultIsFormatDefault()
    {
        Assert.Equal(ContainerFormat.DefaultChunkSize, new PackingOptions().ChunkSize);
    }

    [Fact]
    public void PackingWithoutExplicitSize_WritesDefaultIntoHeader()
    {
        using var ws = new TestWorkspace("default-chunk");
        Directory.CreateDirectory(ws.Source);
        File.WriteAllBytes(Path.Combine(ws.Source, "data.bin"), new byte[200_000]);

        using (var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(), new PackingOptions()))
        {
            packer.Pack(ws.Container, [ws.Source]);
        }

        using var unpacker = new ContainerUnpacker(ws.Container, new ZstdCompressor(3), new Sha256Hasher());
        Assert.Equal(ContainerFormat.DefaultChunkSize, unpacker.ReadInfo().ChunkSize);
    }

    // Границы: слишком маленький и слишком большой чанк отклоняются, а не молча подменяются.
    public static TheoryData<int> OutOfRangeSizes() => new()
    {
        1024,
        ContainerFormat.MinChunkSize - 1,
        ContainerFormat.MaxChunkSize + 1,
    };

    [Theory]
    [MemberData(nameof(OutOfRangeSizes))]
    public void PackingValidation_RejectsOutOfRange(int bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackingOptions(bad, 3).Validate());
    }

    [Theory]
    [MemberData(nameof(OutOfRangeSizes))]
    public void ChunkPlanner_RejectsOutOfRange(int bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChunkPlanner.Create([new SourceEntry("a", "a", 10, false)], bad));
    }

    // Повторная выборочная распаковка в ту же папку: разные файлы одного каталога могут
    // пересекаться по чанкам, и повтор не должен выглядеть как конфликт.
    [Fact]
    public void UnpackSameFileTwice_OverwritesWithCorrectSize()
    {
        using var ws = new TestWorkspace("unpack-twice");
        Directory.CreateDirectory(ws.Source);
        File.WriteAllBytes(Path.Combine(ws.Source, "data.bin"), new byte[200_000]);

        using (var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(), new PackingOptions()))
        {
            packer.Pack(ws.Container, [ws.Source]);
        }

        string twice = Path.Combine(ws.Root, "twice_out");
        using (var unpacker = new ContainerUnpacker(ws.Container, new ZstdCompressor(3), new Sha256Hasher()))
        {
            unpacker.UnpackFile("data.bin", twice);
            unpacker.UnpackFile("data.bin", twice);
        }

        Assert.Equal(200_000, new FileInfo(Path.Combine(twice, "data.bin")).Length);
    }
}
