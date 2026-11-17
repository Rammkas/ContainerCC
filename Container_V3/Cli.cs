using System.Text;

namespace Container_V3;

/// <summary>Разбор аргументов командной строки. Отдельно от логики контейнера.</summary>
public static class Cli
{
    public sealed record Options(string Command, List<string> Positional, string? OutputPath, int ChunkSize, int CompressionLevel);

    public const int DefaultCompressionLevel = ContainerFormat.DefaultCompressionLevel;

    public static Options Parse(string[] args)
    {
        if (args.Length == 0)
            return new Options("", [], null, ContainerFormat.DefaultChunkSize, DefaultCompressionLevel);

        string command = args[0].ToLowerInvariant();
        var positional = new List<string>();
        string? output = null;
        int chunkSize = ContainerFormat.DefaultChunkSize;
        int level = DefaultCompressionLevel;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out":
                    output = RequireValue(args, ref i);
                    break;
                case "-chunk" or "--chunk":
                    chunkSize = ParseSize(RequireValue(args, ref i));
                    break;
                case "-level" or "--level":
                    level = int.Parse(RequireValue(args, ref i));
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        return new Options(command, positional, output, chunkSize, level);
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Не хватает значения после '{args[i]}'.");
        return args[++i];
    }

    /// <summary>Принимает 256K / 1M / 4096. Значение вне границ формата — ошибка пользователя, а не молчаливая подмена.</summary>
    private static int ParseSize(string value)
    {
        string v = value.Trim().ToUpperInvariant();
        int multiplier = 1;
        if (v.EndsWith('K')) { multiplier = 1024; v = v[..^1]; }
        else if (v.EndsWith('M')) { multiplier = 1024 * 1024; v = v[..^1]; }

        if (!int.TryParse(v, out int size))
            throw new ArgumentException($"Не удалось разобрать размер чанка: '{value}'.");

        int chunkSize = checked(size * multiplier);
        if (chunkSize < ContainerFormat.MinChunkSize || chunkSize > ContainerFormat.MaxChunkSize)
            throw new ArgumentException(
                $"Размер чанка {chunkSize:N0} вне допустимого диапазона " +
                $"{ContainerFormat.MinChunkSize:N0}..{ContainerFormat.MaxChunkSize:N0} байт. По умолчанию {ContainerFormat.DefaultChunkSize:N0}.");

        return chunkSize;
    }
}
