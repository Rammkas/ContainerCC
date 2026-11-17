using System.Buffers;

namespace Container_V3.Unpacking;

/// <summary>
/// Прочитать чанк, проверить хэш и разложить его фрагменты в открытые файлы.
/// Одна реализация вместо четырёх в V2 (StoreNSFiles, StoreNSFilesP, ReassembleOneFile, ReassembleOneFileP).
/// </summary>
public sealed class ChunkRestorer : IDisposable
{
    private readonly ContainerReader _reader;
    private readonly IHasher _hasher;
    private readonly int _hashLength;

    public ChunkRestorer(ContainerReader reader, IHasher hasher)
    {
        _reader = reader;
        _hasher = hasher;
        _hashLength = hasher.Length;
    }

    /// <summary>
    /// Обработать один чанк: прочитать сжатые данные целиком (сжатый блок не декомпрессируется по частям),
    /// проверить хэш, распаковать и вырезать все нужные фрагменты.
    /// stagingFiles[i] — временный файл, в который пишется i-й фрагмент задания; один целевой файл
    /// представлен одной частью, принадлежащей одному потоку.
    /// </summary>
    public void Restore(ChunkReadJob job, ICompressor compressor, IReadOnlyList<string> stagingFiles)
    {
        if (stagingFiles.Count != job.Items.Count)
            throw new ContainerFormatException($"Задание на чанк #{job.Chunk.ChunkNumber}: частей {stagingFiles.Count} вместо {job.Items.Count}.");

        ChunkInfo chunk = job.Chunk;

        using PooledBuffer plain = DecompressVerifiedChunk(chunk, compressor);

        // Фрагменты одного чанка пишутся в свои временные части; файл может получать куски из нескольких чанков,
        // но каждая часть открыта ровно одним потоком. Часть создаётся при первом фрагменте и дописывается при следующих.
        Dictionary<string, FileStream>? openParts = null;
        try
        {
            for (int i = 0; i < job.Items.Count; i++)
            {
                (RestoreTarget target, ChunkFragmentRef fragment) = job.Items[i];
                string partPath = stagingFiles[i];

                if (fragment.OffsetInChunk < 0 || fragment.BlockSize <= 0)
                    throw new ContainerFormatException($"Файл '{target.Entry.Path}': некорректный фрагмент (offset {fragment.OffsetInChunk}, size {fragment.BlockSize}).");
                if (fragment.OffsetInChunk + fragment.BlockSize > plain.Length)
                    throw new ContainerFormatException(
                        $"Файл '{target.Entry.Path}': фрагмент [{fragment.OffsetInChunk}..+{fragment.BlockSize}] не помещается в чанк #{chunk.ChunkNumber} длиной {plain.Length}.");

                openParts ??= [];
                if (!openParts.TryGetValue(partPath, out var fs))
                {
                    // Append: тот же поток дописывает следующий фрагмент того же файла в ту же часть.
                    fs = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
                    openParts[partPath] = fs;
                }

                fs.Write(plain.Span.Slice(fragment.OffsetInChunk, fragment.BlockSize));
            }
        }
        finally
        {
            if (openParts is not null)
                foreach (var fs in openParts.Values)
                    fs.Dispose();
        }
    }

    /// <summary>
    /// Проверка чанка без записи на диск: хэш сжатых байтов и декомпрессия до заявленной длины.
    /// Это то, что делает «проверка архива» в оболочках вроде Total Commander (PK_TEST):
    /// данные должны не только распаковаться, но и сойтись по хэшу.
    /// </summary>
    public void VerifyChunk(ChunkInfo chunk, ICompressor compressor)
    {
        using PooledBuffer unused = DecompressVerifiedChunk(chunk, compressor);
    }

    /// <summary>Прочитать сжатый блок чанка, проверить его хэш и распаковать. Буфер отдаёт вызывающему.</summary>
    private PooledBuffer DecompressVerifiedChunk(ChunkInfo chunk, ICompressor compressor)
    {
        long compressedLength = chunk.HashPosition - chunk.OffsetInContainer;
        if (compressedLength <= 0)
            throw new ContainerFormatException($"Чанк #{chunk.ChunkNumber}: некорректная длина сжатых данных {compressedLength}.");
        if (chunk.HashPosition + _hashLength > _reader.Length)
            throw new ContainerFormatException($"Чанк #{chunk.ChunkNumber}: хэш выходит за конец файла.");
        if (chunk.UncompressedLength <= 0)
            throw new ContainerFormatException($"Чанк #{chunk.ChunkNumber}: некорректная длина данных {chunk.UncompressedLength}.");

        using PooledBuffer compressed = new((int)compressedLength);
        _reader.ReadInto(chunk.OffsetInContainer, compressed.Span);

        byte[] storedHash = new byte[_hashLength];
        _reader.ReadInto(chunk.HashPosition, storedHash);

        VerifyHash(chunk, compressed.Span, storedHash);

        PooledBuffer plain = new(chunk.UncompressedLength);
        try
        {
            int decompressed = compressor.Decompress(compressed.Span, plain.Span);
            if (decompressed != chunk.UncompressedLength)
                throw new ContainerFormatException(
                    $"Чанк #{chunk.ChunkNumber}: распаковался в {decompressed} байт вместо {chunk.UncompressedLength}.");
        }
        catch
        {
            plain.Dispose();
            throw;
        }

        return plain;
    }

    private void VerifyHash(ChunkInfo chunk, ReadOnlySpan<byte> compressedData, ReadOnlySpan<byte> storedHash)
    {
        Span<byte> computed = _hashLength <= 256 ? stackalloc byte[_hashLength] : new byte[_hashLength];
        _hasher.Hash(compressedData, computed);

        if (!computed.SequenceEqual(storedHash))
            throw new ChunkIntegrityException(chunk.ChunkNumber);
    }

    public void Dispose() => _reader.Dispose();
}
