using Container_V3.Compressors;
using Container_V3.FileSystem;
using Container_V3.Hashers;
using Container_V3.IO;
using Container_V3.Packing;
using Container_V3.Unpacking;
using System.Buffers.Binary;

namespace Container_V3.Tests;

/// <summary>
/// Проверка на повреждение: испорченный байт в чанке, чужой файл, файл старой версии.
/// Перенос сценария CorruptionCheck из CLI selftest.
/// </summary>
public sealed class CorruptionTests
{
    private static void PackValid(TestWorkspace ws)
    {
        ws.BuildFixture();
        using var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new SourceScanner(),
            new PackingOptions(ContainerFormat.DefaultChunkSize, 3), () => new ZstdCompressor(3));
        packer.Pack(ws.Container, [ws.Source]);
    }

    // Портим один байт внутри последнего чанка (перед хвостовым хэшем),
    // чтобы заголовок и индекс остались нетронутыми.
    [Fact]
    public void DamagedChunkByte_IsDetectedOnUnpack()
    {
        using var ws = new TestWorkspace("corrupt-chunk");
        PackValid(ws);

        byte[] bytes = File.ReadAllBytes(ws.Container);
        bytes[^100] ^= 0xFF;
        string damaged = Path.Combine(ws.Root, "damaged.cc");
        File.WriteAllBytes(damaged, bytes);

        Assert.ThrowsAny<Exception>(() =>
        {
            using var unpacker = new ContainerUnpacker(damaged, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.UnpackAll(Path.Combine(ws.Root, "damaged_out"));
        });
    }

    // Специфичный тип: повреждение чанка ловится именно по хэшу.
    [Fact]
    public void DamagedChunkByte_ThrowsIntegrityOrFormatException()
    {
        using var ws = new TestWorkspace("corrupt-typed");
        PackValid(ws);

        byte[] bytes = File.ReadAllBytes(ws.Container);
        bytes[^100] ^= 0xFF;
        string damaged = Path.Combine(ws.Root, "damaged.cc");
        File.WriteAllBytes(damaged, bytes);

        var ex = Record.Exception(() =>
        {
            using var unpacker = new ContainerUnpacker(damaged, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.UnpackAll(Path.Combine(ws.Root, "damaged_out"));
        });

        Assert.NotNull(ex);
        Assert.True(ex is ChunkIntegrityException or ContainerFormatException,
            $"ожидался ChunkIntegrityException/ContainerFormatException, получено {ex!.GetType().Name}");
    }

    // Файл, который не является контейнером.
    [Fact]
    public void NotAContainer_IsRejected()
    {
        using var ws = new TestWorkspace("corrupt-notcontainer");
        string alien = Path.Combine(ws.Root, "not_a_container.cc");
        File.WriteAllText(alien, "hello world");

        Assert.Throws<ContainerFormatException>(() =>
        {
            using var unpacker = new ContainerUnpacker(alien, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.ReadInfo();
        });
    }

    // Файл старой версии: читается только текущая. Это проверка политики, а не формата —
    // адаптеров V1/V2 здесь нет намеренно.
    [Fact]
    public void LegacyVersionNumber_IsRejected()
    {
        using var ws = new TestWorkspace("corrupt-legacy");
        PackValid(ws);

        byte[] bytes = File.ReadAllBytes(ws.Container);
        byte[] oldVersion = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(oldVersion.AsSpan(4), 2);
        string legacy = Path.Combine(ws.Root, "legacy_v2.cc");
        File.WriteAllBytes(legacy, oldVersion);

        Assert.Throws<ContainerFormatException>(() =>
        {
            using var unpacker = new ContainerUnpacker(legacy, new ZstdCompressor(3), new Sha256Hasher());
            unpacker.ReadInfo();
        });
    }
}
