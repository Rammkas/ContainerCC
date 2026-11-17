using System.Buffers;

namespace Container_V3.IO;

/// <summary>
/// Арендованный массив из ArrayPool с обрезанной длиной.
/// Причина существования: Rent(n) возвращает массив НЕ меньше n (бакеты — степени двойки).
/// В V2 это привело к тому, что сжимали весь массив целиком: ChunkV2.cs:162/372.
/// Здесь лишний хвост физически недоступен через Span.
/// </summary>
public sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;

    public PooledBuffer(int minLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minLength);
        _array = ArrayPool<byte>.Shared.Rent(minLength);
        Length = minLength;
    }

    /// <summary>Ровно запрошенная длина. Арендованный массив может быть длиннее — он не виден.</summary>
    public int Length { get; }

    public Span<byte> Span => _array is null ? throw new ObjectDisposedException(nameof(PooledBuffer)) : _array.AsSpan(0, Length);

    /// <summary>Полный арендованный массив. Нужен, когда принимающая сторона требует byte[] (BinaryReader.ReadBytes).</summary>
    public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    public void Dispose()
    {
        if (_array is null) return;

        // clearArray: true обязателен. Без него в пул возвращается массив с остатками чужих данных,
        // и следующая аренда того же бакета получает мусор вместо нулей — именно так
        // V2 молча тащил «хвост прошлого чанка» в новый.
        ArrayPool<byte>.Shared.Return(_array, clearArray: true);
        _array = null;
    }
}
