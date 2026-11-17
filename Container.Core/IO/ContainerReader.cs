using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Container_V3.IO;

/// <summary>Чтение контейнера: заголовок с проверкой CRC, индекс с проверкой CRC, произвольный диапазон байт.</summary>
public sealed class ContainerReader : IDisposable
{
    private readonly Stream _stream;

    public ContainerReader(Stream stream)
    {
        if (!stream.CanRead) throw new ArgumentException("Поток должен поддерживать чтение.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Формат требует произвольного доступа к файлу.", nameof(stream));
        _stream = stream;
    }

    public ContainerHeader ReadHeader()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        byte[] raw = new byte[ContainerHeader.Size];
        if (_stream.Read(raw) < ContainerHeader.Size)
            throw new ContainerFormatException($"Файл слишком мал для заголовка: {_stream.Length} байт.");

        string magic = Encoding.ASCII.GetString(raw.AsSpan(0, 4));
        if (magic != ContainerHeader.Magic)
            throw new ContainerFormatException($"Неверный формат: ожидалось '{ContainerHeader.Magic}', получено '{magic}'.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        if (version != ContainerHeader.CurrentVersion)
            throw new ContainerFormatException(VersionMessage(version));

        uint storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(32));
        uint actualHeaderCrc = Crc32.HashToUInt32(raw.AsSpan(0, ContainerHeader.CrcCoveredBytes));
        if (storedHeaderCrc != actualHeaderCrc)
            throw new ContainerFormatException($"Заголовок повреждён: CRC {storedHeaderCrc:X8}, ожидалось {actualHeaderCrc:X8}.");

        return new ContainerHeader(
            version,
            BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8)),    // ChunkSize
            BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12)),   // HashLength
            BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(16)),   // CompressionLevel
            BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(20)));  // OffsetToIndex
    }

    /// <summary>
    /// Сообщение о версии. Старые форматы читать не нужно — это итерации разработки,
    /// а не поддерживаемые версии. Поэтому ответ один: «не читается», без попыток угадать
    /// layout V1/V2 по частям. Магическая строка говорит, какой именно это формат.
    /// </summary>
    private static string VersionMessage(int version)
    {
        string detected = version switch
        {
            1 => "V1 (Container.cs)",
            2 => "V2 (ContainerV2.cs)",
            _ => $"с версией {version}",
        };

        return $"Контейнер {detected} не читается: эта реализация пишет и читает только " +
               $"{ContainerHeader.Magic} v{ContainerHeader.CurrentVersion}. Старые форматы — итерации разработки, " +
               "переупакуйте источник этой версией.";
    }

    /// <summary>Прочитать и проверить индекс. Хэш-длина берётся из заголовка файла, а не из текущего IHasher.</summary>
    public ContainerIndex ReadIndex(ContainerHeader header, ICompressor compressor)
    {
        if (header.OffsetToIndex < ContainerHeader.Size)
            throw new ContainerFormatException($"Смещение к индексу внутри заголовка: {header.OffsetToIndex}.");

        _stream.Seek(header.OffsetToIndex, SeekOrigin.Begin);

        Span<byte> prefix = stackalloc byte[16];
        _stream.ReadExactly(prefix);
        long compressedLength = BinaryPrimitives.ReadInt64LittleEndian(prefix);
        long uncompressedLength = BinaryPrimitives.ReadInt64LittleEndian(prefix[8..]);

        if (compressedLength <= 0 || uncompressedLength <= 0)
            throw new ContainerFormatException($"Некорректные размеры индекса: {compressedLength}/{uncompressedLength}.");
        if (uncompressedLength > IndexSerializer.MaxIndexSize)
            throw new ContainerFormatException($"Несжатый индекс слишком большой: {uncompressedLength} байт.");
        if (header.OffsetToIndex + prefix.Length + compressedLength > _stream.Length)
            throw new ContainerFormatException("Индекс обрезан: файл оборван внутри блока индекса.");

        using PooledBuffer compressed = new((int)compressedLength);
        _stream.ReadExactly(compressed.Array, 0, (int)compressedLength);

        // IndexCrc32 лежит в заголовке на смещении 28 относительно начала файла:
        // после OffsetToIndex(8 байт) идёт IndexCrc32(4 байта), затем HeaderCrc32(4 байта).
        _stream.Seek(ContainerHeader.OffsetToIndexField + 8, SeekOrigin.Begin);
        Span<byte> crcField = stackalloc byte[4];
        _stream.ReadExactly(crcField);
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcField);

        uint actualCrc = Crc32.HashToUInt32(compressed.Span[..(int)compressedLength]);
        if (storedCrc != actualCrc)
            throw new ContainerFormatException($"Индекс повреждён: CRC {storedCrc:X8}, ожидалось {actualCrc:X8}.");

        using PooledBuffer plain = new((int)uncompressedLength);
        int written = compressor.Decompress(compressed.Span[..(int)compressedLength], plain.Span);
        if (written != uncompressedLength)
            throw new ContainerFormatException($"Индекс распаковался в {written} байт вместо {uncompressedLength}.");

        return IndexSerializer.Read(plain.Span[..(int)uncompressedLength]);
    }

    /// <summary>Прочитать ровно size байт по абсолютному смещению.</summary>
    public void ReadInto(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(destination);
    }

    /// <summary>Длина файла — нужна, чтобы не читать за его конец при битом индексе.</summary>
    public long Length => _stream.Length;

    public void Dispose() => _stream.Dispose();
}
