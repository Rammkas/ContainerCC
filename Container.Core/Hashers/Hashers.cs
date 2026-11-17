using System.Security.Cryptography;

namespace Container_V3.Hashers;

public sealed class Sha256Hasher : IHasher
{
    public int Length => 32;

    public void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!SHA256.TryHashData(data, destination, out _))
            throw new ArgumentException($"Буфер хэша слишком мал: нужно {Length} байт, получено {destination.Length}.", nameof(destination));
    }
}

public sealed class Sha512Hasher : IHasher
{
    public int Length => 64;

    public void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!SHA512.TryHashData(data, destination, out _))
            throw new ArgumentException($"Буфер хэша слишком мал: нужно {Length} байт, получено {destination.Length}.", nameof(destination));
    }
}
