namespace Container_V3;

/// <summary>Сжатие/распаковка блока. Реализации обязаны возвращать ровно от 0 до buffer.Length байт.</summary>
public interface ICompressor : IDisposable
{
    /// <summary>Максимально возможный размер сжатого блока для inputLength входных байт (для аллокации).</summary>
    int MaxCompressedLength(int inputLength);

    /// <summary>Сжать data в buffer. Вернуть фактическое число записанных байт.</summary>
    int Compress(ReadOnlySpan<byte> data, Span<byte> buffer);

    /// <summary>Распаковать data в buffer. Вернуть фактическое число записанных байт.</summary>
    int Decompress(ReadOnlySpan<byte> data, Span<byte> buffer);
}

/// <summary>Хэш блока. Length — часть формата, а не настройка вызывающего кода.</summary>
public interface IHasher
{
    int Length { get; }
    void Hash(ReadOnlySpan<byte> data, Span<byte> destination);
}

/// <summary>Обход источника. Отдельно — чтобы упаковщик можно было тестировать на списке в памяти.</summary>
public interface ISourceScanner
{
    IEnumerable<SourceEntry> Scan(string rootPath);
}
