namespace Container_V3.FileSystem;

/// <summary>
/// Проверка исходников перед упаковкой.
/// В V2 FillChunkWithDataSync делал "if (read == 0) break;" и молча оставлял нули в чанке:
/// контейнер получался целостным по хэшу, но с дыркой из нулей вместо данных.
/// Здесь расхождение размера обнаруживается до записи, а не после распаковки.
/// </summary>
public static class SourceManifest
{
    public static void Verify(IReadOnlyList<SourceEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;

            if (!File.Exists(entry.FullPath))
                throw new ManifestMismatchException(entry.FullPath, entry.Size, 0);

            long actual = new FileInfo(entry.FullPath).Length;
            if (actual != entry.Size)
                throw new ManifestMismatchException(entry.FullPath, entry.Size, actual);
        }
    }
}
