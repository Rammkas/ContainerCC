using System.IO.Hashing;

namespace Container_V3.Unpacking;

/// <summary>Создание пустых папок и файлов нулевого размера. Отдельно от потока данных чанков.</summary>
public static class EmptyStore
{
    public static void Create(string pathToExtract, IReadOnlyList<FileEntry> emptyEntries)
    {
        string root = Path.GetFullPath(pathToExtract);
        Directory.CreateDirectory(root);

        foreach (var entry in emptyEntries)
        {
            // Сегментами, а не строкой: «\» в имени записи на Android — символ имени, а не папка.
            string absolute = ContainerFileSystem.ToAbsolutePath(root, entry.Path);

            if (entry.IsEmptyFolder)
            {
                Directory.CreateDirectory(absolute);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            using var fs = File.Create(absolute);
        }
    }
}
