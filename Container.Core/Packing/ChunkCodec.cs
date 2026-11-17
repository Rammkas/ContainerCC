using System.Buffers;

namespace Container_V3.Packing;

/// <summary>Один чанк в том виде, в каком он уходит в контейнер.</summary>
public sealed record EncodedChunk(int ChunkNumber, byte[] CompressedData, byte[] Hash, int UncompressedLength);

/// <summary>
/// Сжатие и хэширование чанка. Здесь исправлен главный баг V2:
/// V2 вызывал compressor.Compress(арендованныйМассив) — ArrayPool.Rent(N) возвращает массив НЕ короче N,
/// поэтому при chunkSize вне степени двойки в чанк попадали лишние байты пула (ChunkV2.cs:162, :372).
/// Здесь всегда передаётся ровно [0..uncompressedLength).
/// </summary>
public static class ChunkCodec
{
    public static EncodedChunk Encode(List<ChunkFragment> fragmentsInChunk, int chunkSize, ICompressor compressor, IHasher hasher, FileReadPool files)
    {
        int uncompressedLength = ChunkPlanner.UncompressedLengthOf(fragmentsInChunk, chunkSize);

        using PooledBuffer plain = new(uncompressedLength);
        ChunkFiller.Fill(fragmentsInChunk, plain.Span, files);

        // Буфер под сжатие берём из пула, но в файл уходит точный массив фактической длины:
        // лишние байты "запасного" массива не должны попасть в контейнер.
        byte[] pooled = ArrayPool<byte>.Shared.Rent(compressor.MaxCompressedLength(uncompressedLength));
        int compressedLength;
        try
        {
            compressedLength = compressor.Compress(plain.Span, pooled.AsSpan(0, pooled.Length));

            if (compressedLength <= 0 || compressedLength > pooled.Length)
                throw new ContainerFormatException($"Сжатие вернуло {compressedLength} байт при лимите {pooled.Length}.");

            byte[] data = new byte[compressedLength];
            Array.Copy(pooled, data, compressedLength);

            byte[] hash = new byte[hasher.Length];
            hasher.Hash(data, hash);

            return new EncodedChunk(fragmentsInChunk.Count > 0 ? fragmentsInChunk[0].ChunkIndex : -1, data, hash, uncompressedLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled, clearArray: true);
        }
    }
}
