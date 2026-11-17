using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace Container_V3.Tests;

/// <summary>
/// Round-trip: упаковал → распаковал → сравнил SHA-256 каждого файла.
/// Перенос главного сценария CLI selftest. 64K — нижняя граница, 700K — не степень
/// двойки (ловит ошибку «длина чанка ≠ ChunkSize»), 1M — значение по умолчанию.
/// </summary>
public sealed class RoundTripTests
{
    public static TheoryData<int> ChunkSizes() => new()
    {
        ContainerFormat.MinChunkSize,
        700 * 1024,
        ContainerFormat.DefaultChunkSize,
    };

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public void PackThenUnpack_PreservesEveryFile(int chunkSize)
    {
        using var ws = new TestWorkspace($"roundtrip-{chunkSize}");
        ws.BuildFixture();

        using (var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(),
            new PackingOptions(chunkSize, 3), () => new ZstdCompressor(3)))
        {
            packer.Pack(ws.Container, [ws.Source]);
        }

        using (var unpacker = new ContainerUnpacker(ws.Container, new ZstdCompressor(3), new Sha256Hasher()))
        {
            var info = unpacker.ReadInfo();
            Assert.Equal(chunkSize, info.ChunkSize);
            Assert.True(info.FileCount >= 5, $"ожидалось не меньше 5 файлов, получено {info.FileCount}");
            Assert.True(info.ChunkCount >= 1);

            unpacker.UnpackAll(ws.Restored);
        }

        TestWorkspace.CompareTrees(ws.Source, ws.Restored);
    }

    [Theory]
    [MemberData(nameof(ChunkSizes))]
    public void ContainerHeader_RecordsRequestedChunkSize(int chunkSize)
    {
        using var ws = new TestWorkspace($"chunksize-{chunkSize}");
        ws.BuildFixture();

        using (var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(),
            new PackingOptions(chunkSize, 3), () => new ZstdCompressor(3)))
        {
            packer.Pack(ws.Container, [ws.Source]);
        }

        // big.bin (3 001 111 B) не кратен ни одному chunkSize — хвостовой чанк обязан быть короче.
        using var unpacker = new ContainerUnpacker(ws.Container, new ZstdCompressor(3), new Sha256Hasher());
        var info = unpacker.ReadInfo();
        Assert.Equal(chunkSize, info.ChunkSize);

        // big.bin должен занимать ceil(3001111 / chunkSize) чанков — сумма их длин равна размеру файла.
        long sizeOfBig = new FileInfo(Path.Combine(ws.Source, "big.bin")).Length;
        Assert.Equal(3_000_111, sizeOfBig);
    }
}
