namespace Container_V3.Packing;

/// <summary>
/// Открытые исходные файлы, переиспользуемые внутри одного чанка.
///
/// Зачем существует. Файл среднего размера попадает в несколько чанков подряд, и V2 открывал его
/// заново на каждый фрагмент: FillChunkWithDataSync / MT-копия делали new FileStream внутри цикла
/// по фрагментам. Плюс каждая копия читала фрагмент во временный массив и затем копировала его
/// в буфер чанка — то есть те же байты проходили через память дважды. Здесь файл открывается один
/// раз на чанк, а данные пишутся прямо в буфер чанка.
///
/// Почему безопасно при параллельной упаковке. Чанки кодируются в пуле потоков, и один файл мог бы
/// читаться двумя потоками одновременно. Поэтому пул живёт ровно на время одного чанка: он создаётся
/// в теле Parallel.For и закрывается вместе с ним. Ни один FileStream не переходит между потоками,
/// замка нет, и порядок чтения внутри чанка не зависит от того, какой поток его обслуживает.
/// </summary>
public sealed class FileReadPool : IDisposable
{
    private const int ChunkBufferBytes = 1 << 16;

    // Ключ — путь источника. Файлы в дереве уникальны по пути, а сравнение регистронезависимое,
    // потому что Windows так и трактует имена: иначе один и тот же файл мог бы открыться дважды
    // из-за разной записи пути в плане.
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Прочитать [sourceOffset, sourceOffset+destination.Length) прямо в буфер чанка.
    /// Позиция ставится явно, поэтому фрагменты можно читать в любом порядке.
    /// </summary>
    public void ReadIntoChunk(string path, long sourceOffset, Span<byte> destination)
    {
        if (!_open.TryGetValue(path, out var fs))
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBufferBytes, FileOptions.SequentialScan);
            _open[path] = fs;
        }

        if (fs.Seek(sourceOffset, SeekOrigin.Begin) != sourceOffset)
            throw new ManifestMismatchException(path, sourceOffset + destination.Length, fs.Length);

        // ReadExactly повторяет чтение до конца диапазона сам. Короткий EOF без повтора — это ровно
        // та «тихая нулевая дыра», которую V2 оставлял в чанке через "if (read == 0) break;";
        // здесь она становится явным расхождением размера.
        try
        {
            fs.ReadExactly(destination);
        }
        catch (EndOfStreamException)
        {
            throw new ManifestMismatchException(path, sourceOffset + destination.Length, fs.Position);
        }
    }

    public void Dispose()
    {
        foreach (var fs in _open.Values)
            fs.Dispose();
        _open.Clear();
    }
}
