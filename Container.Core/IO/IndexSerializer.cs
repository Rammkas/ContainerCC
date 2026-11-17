using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Container_V3.IO;

/// <summary>
/// Сериализация индекса в байты и обратно. Только формат, без понимания контейнера.
/// Строка: [int32 длина в байтах][UTF-8 байты] — вместо varint-префикса от BinaryWriter.Write(string),
/// который V2 наследовал неявно и который обязан совпадать на любой стороне чтения.
/// </summary>
public static class IndexSerializer
{
    public const int BlockOffsetsSize = 16;

    /// <summary>Разумный предел: индекс — метаданные, а не пользовательские данные.</summary>
    public const int MaxIndexSize = 256 * 1024 * 1024;

    public static byte[] Write(ContainerIndex index)
    {
        // Строки кодируем сами ([int32 длина][UTF-8]), поэтому BinaryWriter.Write(string)
        // с его 7-bit varint и динамическим ростом буфера здесь не используется вовсе.
        using var ms = new MemoryStream(estimateSize(index));

        // Два смещения к блокам. Пока нули: они станут известны после записи самих блоков.
        Span<byte> offsets = stackalloc byte[BlockOffsetsSize];
        offsets.Clear();
        ms.Write(offsets);

        // Смещение начала блока чанков — это смещение его счётчика; сами описания начинаются на 4 байта позже.
        long chunksBlockStart = ms.Position;
        WriteInt32(ms, index.Chunks.Count);
        foreach (var c in index.Chunks)
        {
            WriteInt32(ms, c.ChunkNumber);          // int32
            WriteInt64(ms, c.OffsetInContainer);    // int64
            WriteInt64(ms, c.HashPosition);         // int64
            WriteInt32(ms, c.UncompressedLength);   // int32
        }

        long filesBlockStart = ms.Position;
        WriteInt32(ms, index.Files.Count);
        foreach (var f in index.Files)
        {
            WritePath(ms, f.Path);                  // [int32 длина][UTF-8] — ровно как читается обратно
            WriteInt64(ms, f.Size);                 // int64
            ms.WriteByte(f.IsEmptyFolder ? (byte)1 : (byte)0);  // bool: 1 байт, не 4 как у BinaryWriter
            ms.WriteByte(f.IsSplitted ? (byte)1 : (byte)0);     // bool: 1 байт
            WriteInt32(ms, f.Fragments.Count);      // int32

            foreach (var fr in f.Fragments)
            {
                WriteInt32(ms, fr.ChunkNumber);     // int32
                WriteInt32(ms, fr.OffsetInChunk);   // int32: смещение внутри чанка не превышает ChunkSize
                WriteInt32(ms, fr.BlockSize);       // int32  => запись ровно 12 байт
            }
        }

        ms.Flush();

        // Дописываем смещения к блокам. Относительно начала индекса — как в V2.
        byte[] result = ms.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(0, 8), chunksBlockStart);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8, 8), filesBlockStart);
        return result;
    }

    /// <summary>Явная 32-битная длина строки вместо varint от BinaryWriter.Write(string).</summary>
    private static void WritePath(Stream stream, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, 1 << 20);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static int estimateSize(ContainerIndex index)
    {
        long size = BlockOffsetsSize + 4L + (long)index.Chunks.Count * 24 + 4;
        foreach (var f in index.Files)
            size += 4 + Encoding.UTF8.GetByteCount(f.Path) + 14 + (long)f.Fragments.Count * 12;

        // Запас на случай расхождения оценки с фактической записью.
        return (int)Math.Min(size * 2 + 256, int.MaxValue);
    }

    public static ContainerIndex Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockOffsetsSize)
            throw new ContainerFormatException("Индекс короче 16 байт: читать нечего.");

        long chunksOffset = BinaryPrimitives.ReadInt64LittleEndian(data[..8]);
        long filesOffset = BinaryPrimitives.ReadInt64LittleEndian(data[8..16]);

        if (chunksOffset < BlockOffsetsSize || chunksOffset > data.Length)
            throw new ContainerFormatException($"Смещение блока чанков вне индекса: {chunksOffset} (длина индекса {data.Length}).");
        if (filesOffset < BlockOffsetsSize || filesOffset > data.Length)
            throw new ContainerFormatException($"Смещение блока файлов вне индекса: {filesOffset} (длина индекса {data.Length}).");

        var chunks = ReadChunks(data[(int)chunksOffset..]);
        var files = ReadFiles(data[(int)filesOffset..]);
        return new ContainerIndex(files, chunks);
    }

    private static List<ChunkInfo> ReadChunks(ReadOnlySpan<byte> data)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        if (count < 0 || count > (data.Length - 4) / 24)
            throw new ContainerFormatException($"Несовместимое количество чанков {count} для блока в {data.Length} байт.");

        var list = new List<ChunkInfo>(count);
        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            list.Add(new ChunkInfo(
                BinaryPrimitives.ReadInt32LittleEndian(data[pos..]),
                BinaryPrimitives.ReadInt64LittleEndian(data[(pos + 4)..]),
                BinaryPrimitives.ReadInt64LittleEndian(data[(pos + 12)..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 20)..])));
            pos += 24;
        }
        return list;
    }

    private static List<FileEntry> ReadFiles(ReadOnlySpan<byte> data)
    {
        // Контракт: смещение блока указывает на его счётчик. Счётчик читает сам ReadFiles,
        // поэтому данные начинаются сразу после него (см. также ReadChunks).
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        if (count < 0)
            throw new ContainerFormatException($"Отрицательное количество файлов: {count}.");

        var list = new List<FileEntry>(Math.Min(count, 1024));
        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            if (pos + 4 > data.Length)
                throw new ContainerFormatException("Индекс оборван внутри записи файла.");

            int pathLen = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            if (pathLen < 0 || pos + 4 + pathLen + 14 > data.Length)
                throw new ContainerFormatException($"Некорректная длина пути {pathLen} в записи файла #{i}.");

            string path = Encoding.UTF8.GetString(data[(pos + 4)..(pos + 4 + pathLen)]);
            pos += 4 + pathLen;

            long size = BinaryPrimitives.ReadInt64LittleEndian(data[pos..]);
            bool isEmptyFolder = data[pos + 8] != 0;
            bool isSplitted = data[pos + 9] != 0;
            int fragmentCount = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 10)..]);
            pos += 14;

            if (fragmentCount < 0 || fragmentCount > (data.Length - pos) / 12)
                throw new ContainerFormatException($"Несовместимое количество фрагментов {fragmentCount} у файла '{path}'.");

            var fragments = new List<ChunkFragmentRef>(fragmentCount);
            for (int j = 0; j < fragmentCount; j++)
            {
                fragments.Add(new ChunkFragmentRef(
                    BinaryPrimitives.ReadInt32LittleEndian(data[pos..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 8)..])));
                pos += 12;
            }

            list.Add(new FileEntry(path, size, isEmptyFolder, isSplitted, fragments));
        }
        return list;
    }
}
