using System;
using System.Collections.Generic;
using Container_V3;

namespace ContainerWcx;

/// <summary>Одна строка, которую увидит панель Total Commander.</summary>
internal sealed record ListingEntry(string RelativePath, long Size, long PackedSize, bool IsDirectory);

/// <summary>
/// Индекс контейнера → список панели.
///
/// В индексе описаны файлы и пустые папки, но не промежуточные каталоги: запись «a\b\c.bin» не
/// означает, что в контейнере есть запись «a». Панель TC строит дерево по записям списка, поэтому
/// недостающие каталоги добавляем здесь — иначе вложенные файлы показывались бы одной строкой
/// «a\b\c.bin», а пустые папки не было бы видно вовсе.
/// </summary>
internal static class ArchiveListing
{
    public static List<ListingEntry> Build(ContainerIndex index)
    {
        DirectoryNode root = new(string.Empty, string.Empty);

        foreach (FileEntry file in index.Files)
        {
            string path = NormalizePath(file.Path);
            if (file.IsEmptyFolder)
                root.AddDirectory(path, file.Path);
            else
                root.AddFile(path, file);
        }

        var result = new List<ListingEntry>();
        Collect(root, index, result);
        return result;
    }

    /// <summary>
    /// Путь из индекса к виду панели: одно правило на весь проект (ContainerPath) — CLI, модификатор
    /// и список панели обязаны называть запись одинаково, иначе запись, добавленную через TC, нельзя
    /// будет удалить или распаковать по имени. Контейнер V3 пишет относительные пути с «\», но читать
    /// мы обязаны не доверять ничему: «..», диск и неразрешимые символы отклоняются здесь же.
    /// Длина тоже проверяется здесь: ContainerPath.Normalize отсекает имена длиннее 1024 символов,
    /// а FileName в tHeaderDataExW ровно 1024 — большее имя TC показал бы обрезанным, и по обрезанному
    /// имени запись уже ни достать, ни удалить.
    /// </summary>
    public static string NormalizePath(string raw) => ContainerPath.Normalize(raw);

    private static void Collect(DirectoryNode node, ContainerIndex index, List<ListingEntry> output)
    {
        foreach (DirectoryNode child in node.Children.Values)
        {
            if (child.File is not null && child.Children.Count > 0)
                throw new ContainerFormatException(
                    $"Индекс конфликтует: '{child.FullPath}' описан и как файл, и как папка.");

            // Папка из индекса помечена IsDirectory; промежуточные каталоги («a\b\c.bin» без записи «a»)
            // узнаём по дочерним элементам — без них панель TC не соберёт дерево папок.
            if (child.IsDirectory || child.Children.Count > 0)
                output.Add(new ListingEntry(child.FullPath, 0, 0, true));

            if (child.File is not null)
                output.Add(new ListingEntry(child.FullPath, child.File.Size, PackedSize(child.File, index), false));

            Collect(child, index, output);
        }
    }

    /// <summary>
    /// «Размер в контейнере» для колонки TC. Точного размера сжатых данных на файл индекс не хранит:
    /// чанк общий для нескольких файлов, поэтому берём долю чанка по длине фрагмента.
    /// Оценка, и она честная: 0 для непустого файла TC показал бы как «данных нет».
    /// </summary>
    private static long PackedSize(FileEntry file, ContainerIndex index)
    {
        if (file.Fragments.Count == 0 || file.Size == 0)
            return 0;

        long total = 0;
        foreach (ChunkFragmentRef fragment in file.Fragments)
        {
            if (fragment.ChunkNumber < 0 || fragment.ChunkNumber >= index.Chunks.Count)
                throw new ContainerFormatException($"Файл '{file.Path}' ссылается на чанк #{fragment.ChunkNumber}, которого нет в индексе.");

            ChunkInfo chunk = index.Chunks[fragment.ChunkNumber];
            long compressed = chunk.HashPosition - chunk.OffsetInContainer;
            if (compressed <= 0 || chunk.UncompressedLength <= 0)
                throw new ContainerFormatException($"Чанк #{chunk.ChunkNumber}: некорректные длины ({compressed} / {chunk.UncompressedLength}).");

            total += compressed * fragment.BlockSize / chunk.UncompressedLength;
        }

        return total > 0 ? total : 1;
    }

    /// <summary>
    /// Узел дерева путей. Один и тот же путь файлом и папкой одновременно быть не может:
    /// на NTFS это конфликт, и контейнер с таким индексом — повреждённый, а не «странный».
    /// </summary>
    private sealed class DirectoryNode
    {
        public DirectoryNode(string name, string fullPath)
        {
            Name = name;
            FullPath = fullPath;
        }

        public string Name { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; private set; }
        public FileEntry? File { get; private set; }
        public SortedDictionary<string, DirectoryNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, FileEntry file)
        {
            DirectoryNode node = this;

            foreach (string segment in path.Split('\\'))
            {
                node.ConflictCheck(path);
                node = node.Child(segment, path);
            }

            node.ConflictCheck(path);

            if (node.File is not null)
                throw new ContainerFormatException($"Индекс содержит дубликат файла '{path}'.");

            node.File = file;
        }

        public void AddDirectory(string path, string originalPath)
        {
            DirectoryNode node = this;

            foreach (string segment in path.Split('\\'))
            {
                node.ConflictCheck(path);
                node = node.Child(segment, path);
            }

            node.ConflictCheck(path);
            node.IsDirectory = true;

            // Пустая папка, которой уже дали имя файла, — это конфликт формата, а не «пустая папка».
            if (node.File is not null && !node.File.IsEmptyFolder)
                throw new ContainerFormatException(
                    $"Индекс конфликтует: '{node.FullPath}' описан и как файл, и как папка ('{originalPath}').");
        }

        private DirectoryNode Child(string segment, string path)
        {
            if (!Children.TryGetValue(segment, out var child))
            {
                string childPath = FullPath.Length == 0 ? segment : FullPath + "\\" + segment;
                child = new DirectoryNode(segment, childPath);
                Children[segment] = child;
            }

            return child;
        }

        private void ConflictCheck(string path)
        {
            if (IsDirectory && File is not null && !File.IsEmptyFolder)
                throw new ContainerFormatException(
                    $"Индекс конфликтует: '{FullPath}' описан и как файл, и как папка (запись '{path}').");
        }
    }
}
