using System.Security.Cryptography;
using System.Text;

namespace Container_V3.Tests;

/// <summary>
/// Общий временный каталог для тестов (аналог Reset/BuildFixture из CLI selftest).
/// Каждый тест получает изолированную папку и удаляет её в Dispose, чтобы параллельные
/// тесты xUnit не мешали друг другу.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    public string Source => Path.Combine(Root, "src");
    public string Restored => Path.Combine(Root, "out");
    public string Container => Path.Combine(Root, "test.cc");

    public TestWorkspace(string name = "work")
    {
        Root = Path.Combine(
            Path.GetTempPath(), "container_v3_tests",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Тот же фикстуар, что и в SelfTest: файл, делящий чанк пополам, пустой файл,
    /// пустые папки, псевдослучайные несжимаемые данные и кириллическое имя.
    /// </summary>
    public void BuildFixture()
    {
        Directory.CreateDirectory(Source);

        File.WriteAllText(Path.Combine(Source, "small.txt"), new string('a', 100));
        File.WriteAllText(Path.Combine(Source, "zero.txt"), "");
        Directory.CreateDirectory(Path.Combine(Source, "empty_dir"));
        Directory.CreateDirectory(Path.Combine(Source, "nested", "deep", "also_empty"));

        var rng = new Random(12345);
        byte[] big = new byte[3_000_111]; // не кратно ни одному chunkSize
        rng.NextBytes(big);
        File.WriteAllBytes(Path.Combine(Source, "big.bin"), big);

        byte[] compressible = Encoding.UTF8.GetBytes(new string('x', 250_000));
        File.WriteAllBytes(Path.Combine(Source, "nested", "repeat.dat"), compressible);

        byte[] medium = new byte[131_072]; // ровно 128K — граница чанка при 64K
        rng.NextBytes(medium);
        File.WriteAllBytes(Path.Combine(Source, "nested", "deep", "medium.bin"), medium);

        File.WriteAllText(Path.Combine(Source, "тестовый файл.txt"), "привет, контейнер");
    }

    /// <summary>Сравнение двух деревьев по SHA-256 каждого файла + пустые папки.</summary>
    public static void CompareTrees(string expectedRoot, string actualRoot)
    {
        var expected = Collect(expectedRoot);
        var actual = Collect(actualRoot);

        foreach (var (path, hash) in expected)
        {
            Assert.True(actual.TryGetValue(path, out var actualHash),
                $"нет файла после распаковки: {path}");
            Assert.Equal(hash, actualHash);
        }

        foreach (var path in actual.Keys)
            Assert.True(expected.ContainsKey(path), $"лишний файл после распаковки: {path}");

        // Пустые папки обязаны выжить.
        foreach (string dir in Directory.GetDirectories(expectedRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(expectedRoot, dir);
            Assert.True(Directory.Exists(Path.Combine(actualRoot, relative)),
                $"пустая папка потеряна: {relative}");
        }
    }

    public static Dictionary<string, string> Collect(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return map;
    }

    public static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static void WriteRandom(string path, int size, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[size];
        rng.NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // временный мусор не должен ронять тест
        }
    }
}
