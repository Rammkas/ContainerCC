using System.Buffers.Binary;
using System.IO.Hashing;

namespace Container_V3.Packing;

/// <summary>
/// Запись индекса в конец файла и указателя на него в заголовке.
///
/// Этот шаг делают и упаковщик, и модификация контейнера — дублировать его нельзя, потому что
/// именно эти 16 байт заголовка (OffsetToIndex + IndexCrc32 + HeaderCrc32) решают, какому
/// индексу верить. Ошибка в одном месте дала бы контейнер, который читается старым индексом
/// вместо нового.
/// </summary>
public static class IndexWriter
{
    /// <summary>Записать индекс и дописать заголовок. Возвращает смещение нового индекса.</summary>
    public static long Write(ContainerWriter writer, ContainerIndex index, ICompressor compressor, int chunkSize, int hashLength, int compressionLevel)
    {
        byte[] plain = IndexSerializer.Write(index);

        using PooledBuffer compressed = new(compressor.MaxCompressedLength(plain.Length));
        int compressedLength = compressor.Compress(plain, compressed.Span);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compressedLength, compressed.Span.Length);

        long offsetToIndex = writer.Position;

        Span<byte> prefix = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(prefix, compressedLength);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], plain.Length);

        writer.Write(prefix);
        writer.Write(compressed.Span[..compressedLength]);

        uint indexCrc = Crc32.HashToUInt32(compressed.Span[..compressedLength]);

        // Данные должны оказаться на диске раньше, чем заголовок укажет на новый индекс:
        // иначе обрыв между «индекс записан» и «заголовок исправлен» мог бы оставить заголовок,
        // указывающий на индекс, которого на диске ещё нет.
        writer.FlushToDisk();
        writer.PatchHeader(chunkSize, hashLength, compressionLevel, offsetToIndex, indexCrc);

        return offsetToIndex;
    }
}
