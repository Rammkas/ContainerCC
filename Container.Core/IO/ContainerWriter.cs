using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Container_V3.IO;

/// <summary>
/// Низкоуровневая запись контейнера: заголовок, чанки, индекс.
/// Единственный источник истины о позиции — _position. Позиция потока не используется как истина,
/// потому что V2 местами писал через BinaryWriter, а местами через Stream.WriteTo, обходя его буфер.
/// Здесь всё идёт через один счётчик, и он синхронизируется с потоком на каждом Seek.
/// </summary>
public sealed class ContainerWriter : IDisposable
{
    private readonly Stream _stream;
    private long _position;
    private bool _headerPatched = true;

    public ContainerWriter(Stream stream)
    {
        if (!stream.CanWrite) throw new ArgumentException("Поток должен поддерживать запись.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Поток должен поддерживать Seek: смещение к индексу известно только после записи данных.", nameof(stream));
        _stream = stream;
        _position = stream.Position;
    }

    /// <summary>Позиция, в которую пойдёт следующая запись.</summary>
    public long Position => _position;

    public void Write(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        _position += data.Length;
    }

    /// <summary>Перестановка позиции. Flush обязателен: без него буферизованный слой допишет накопленное в новое место.</summary>
    public void Seek(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        _stream.Flush();
        _stream.Seek(position, SeekOrigin.Begin);
        _position = position;
    }

    /// <summary>
    /// Вытолкать записанное на диск, а не только в буфер потока.
    /// Нужно перед правкой заголовка: заголовок — точка коммита, и то, на что он указывает,
    /// должно быть на диске раньше самого указания. Иначе обрыв питания мог бы оставить
    /// заголовок, указывающий на индекс, который до диска не дошёл.
    /// </summary>
    public void FlushToDisk()
    {
        _stream.Flush();

        // Flush(flushToDisk) есть только у FileStream; у абстрактного Stream его нет.
        // Для нашего контейнера поток всегда файловый, но проверка на тип оставляет метод
        // честным для любого Stream: буферизация выталкивается в любом случае.
        if (_stream is FileStream file)
            file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Записать заголовок. Поля OffsetToIndex и IndexCrc32 остаются нулевыми:
    /// они станут известны только после записи всех чанков и индекса.
    /// </summary>
    public void WriteHeader(int chunkSize, int hashLength, int compressionLevel)
    {
        Span<byte> header = stackalloc byte[ContainerHeader.Size];
        BuildHeader(chunkSize, hashLength, compressionLevel, 0L, 0u, header);

        // Пока OffsetToIndex и IndexCrc32 нулевые — но CRC считаем по тем же 32 байтам,
        // что и при исправлении ниже. Значение всё равно будет перезаписано в PatchHeader.
        Crc32.Hash(header[..ContainerHeader.CrcCoveredBytes], header[ContainerHeader.CrcCoveredBytes..]);

        Write(header);

        // Отмечает дыру, которую обязан закрыть PatchHeader: иначе контейнер можно закрыть
        // и не дописать индекс — файл выглядит как контейнер, но читать его нельзя.
        _headerPatched = false;
    }

    /// <summary>
    /// Дописать OffsetToIndex, IndexCrc32 и пересчитанный HeaderCrc32.
    /// Первые 20 байт заголовка постоянны, поэтому с смещения 20 переписываем сразу 16 байт:
    /// OffsetToIndex(8) + IndexCrc32(4) + HeaderCrc32(4).
    /// </summary>
    public void PatchHeader(int chunkSize, int hashLength, int compressionLevel, long offsetToIndex, uint indexCrc32)
    {
        Span<byte> header = stackalloc byte[ContainerHeader.Size];
        BuildHeader(chunkSize, hashLength, compressionLevel, offsetToIndex, indexCrc32, header);

        // HeaderCrc32 считается по первым 32 байтам уже исправленного заголовка.
        Crc32.Hash(header[..ContainerHeader.CrcCoveredBytes], header[ContainerHeader.CrcCoveredBytes..]);

        Seek(ContainerHeader.OffsetToIndexField);
        Write(header[ContainerHeader.OffsetToIndexField..]);
        _stream.Flush();

        _headerPatched = true;
    }

    private static void BuildHeader(int chunkSize, int hashLength, int compressionLevel, long offsetToIndex, uint indexCrc32, Span<byte> destination)
    {
        Encoding.ASCII.GetBytes(ContainerHeader.Magic, destination);                              // 0..3
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], ContainerHeader.CurrentVersion); // 4..7
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], chunkSize);                       // 8..11
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], hashLength);                     // 12..15
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], compressionLevel);               // 16..19
        BinaryPrimitives.WriteInt64LittleEndian(destination[20..], offsetToIndex);                  // 20..27
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], indexCrc32);                    // 28..31
    }

    /// <summary>
    /// Операция сорвана до точки коммита: индекс дописывать уже нечем. Без этого Dispose
    /// выдал бы «Контейнер не завершён» вместо настоящей причины (Stop пользователя, битый
    /// источник, конец диска), а её как раз и нужно показать наружу.
    /// </summary>
    public void Abandon() => _headerPatched = true;

    public void Dispose()
    {
        // Незакрытая дыра в заголовке означает, что индекс не был дописан. Такой файл нельзя
        // оставлять как «готовый контейнер»: это ровно тот случай, когда V2 молча выдавал
        // обрезанный файл. Требование проверяется здесь, пока поток ещё открыт.
        if (!_headerPatched)
            throw new ContainerFormatException("Контейнер не завершён: индекс и OffsetToIndex не дописаны в заголовок.");

        _stream.Flush();
        // Поток передан извне — не закрываем его здесь.
    }
}
