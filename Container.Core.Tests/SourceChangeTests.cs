using Container_V3.Compressors;
using Container_V3.Hashers;
using Container_V3.IO;
using Container_V3.Packing;
using Container_V3.Unpacking;

namespace Container_V3.Tests;

/// <summary>
/// Проверка тихой нулевой дыры. V2 делал «if (read == 0) break;» при заполнении чанка:
/// контейнер получался целостным по хэшу, но вместо данных лежали нули.
/// Сценарий воспроизводится намеренно — через фиктивный сканер, который заявляет файл
/// длиннее, чем он есть на самом деле. Упаковка обязана упасть до записи.
/// </summary>
public sealed class SourceChangeTests
{
    /// <summary>Сканер, который завышает размер файла — модель «источник изменился после сканирования».</summary>
    private sealed class OverstatingScanner : ISourceScanner
    {
        private readonly string _root;
        public OverstatingScanner(string root) => _root = root;

        public IEnumerable<SourceEntry> Scan(string path)
        {
            string file = Path.Combine(_root, "short.bin");
            yield return new SourceEntry(file, "short.bin", new FileInfo(file).Length + 10_000, false);
        }
    }

    [Fact]
    public void Pack_FailsBeforeWriting_WhenSourceIsShorterThanDeclared()
    {
        using var ws = new TestWorkspace("hole");
        Directory.CreateDirectory(ws.Source);

        byte[] real = new byte[50_000];
        new Random(7).NextBytes(real);
        File.WriteAllBytes(Path.Combine(ws.Source, "short.bin"), real);

        using var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new OverstatingScanner(ws.Source),
            new PackingOptions(ContainerFormat.MinChunkSize, 3));

        Assert.Throws<ManifestMismatchException>(() => packer.Pack(ws.Container, [ws.Source]));
    }

    // Незавершённый контейнер не должен оставаться как «готовый»: writer обязан
    // отказаться завершать запись, если индекс не дописан.
    [Fact]
    public void Writer_RefusesToFinish_WithoutIndex()
    {
        using var ws = new TestWorkspace("partial-write");
        string partial = Path.Combine(ws.Root, "partial.cc");

        Assert.Throws<ContainerFormatException>(() =>
        {
            using var raw = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new ContainerWriter(raw);
            writer.WriteHeader(ContainerFormat.MinChunkSize, 32, 3);
            writer.Write(new byte[1024]); // чанк записали, индекс — нет
        });
    }

    [Fact]
    public void HalfWrittenContainer_IsRejectedOnRead()
    {
        using var ws = new TestWorkspace("hole-read");
        Directory.CreateDirectory(ws.Source);

        byte[] real = new byte[50_000];
        new Random(7).NextBytes(real);
        File.WriteAllBytes(Path.Combine(ws.Source, "short.bin"), real);

        string hole = Path.Combine(ws.Root, "hole.cc");
        using (var packer = new ContainerPacker(
            new ZstdCompressor(3), new Sha256Hasher(), new OverstatingScanner(ws.Source),
            new PackingOptions(ContainerFormat.MinChunkSize, 3)))
        {
            Record.Exception(() => packer.Pack(hole, [ws.Source]));
        }

        if (File.Exists(hole) && new FileInfo(hole).Length > 0)
        {
            Assert.Throws<ContainerFormatException>(() =>
            {
                using var unpacker = new ContainerUnpacker(hole, new ZstdCompressor(3), new Sha256Hasher());
                unpacker.ReadInfo();
            });
        }
    }
}
