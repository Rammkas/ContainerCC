using ZstdSharp;

namespace Container_V3.Compressors;

/// <summary>
/// Обёртка ZstdSharp над span-API.
/// Ключевое отличие от V2: интерфейс возвращает не Span&lt;byte&gt;, а число байт, записанных в переданный буфер.
/// В V2 Compress(byte[]) всегда аллокировал новый массив и, что важнее, вызывался от ВСЕГО арендованного
/// массива ArrayPool (Rent возвращает бакет не меньше запрошенного) — см. ChunkWriter.
/// </summary>
public sealed class ZstdCompressor(int compressionLevel = 3) : ICompressor, IDisposable
{
    private readonly Compressor _compressor = new(compressionLevel);
    private readonly Decompressor _decompressor = new();
    private bool _disposed;

    public int MaxCompressedLength(int inputLength) => Compressor.GetCompressBound(inputLength);

    public int Compress(ReadOnlySpan<byte> data, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _compressor.Wrap(data, buffer);
    }

    public int Decompress(ReadOnlySpan<byte> data, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _decompressor.Unwrap(data, buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _compressor.Dispose();
        _decompressor.Dispose();   // V2 этот Decompressor неdispose'ил никогда
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
