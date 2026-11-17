using Android.Content;
using Android.OS;

namespace ContainerCC.Mobile;

/// <summary>
/// Доступ к общей памяти телефона.
///
/// Android 11+ : разрешение «Все файлы» (MANAGE_EXTERNAL_STORAGE). Без него приложение видит
/// только свою папку, и контейнеры нельзя держать рядом с остальными файлами — то есть рядом
/// с тем же, что показывает Total Commander для Android.
///
/// Android 7..10: READ/WRITE_EXTERNAL_STORAGE, запрашиваются у пользователя при запуске.
/// </summary>
public static class StorageAccess
{
    /// <summary>Папка, с которой начинаем: общая память, та же, что в Total Commander для Android.</summary>
    public static string StartDirectory()
    {
#pragma warning disable CS0618 // ExternalStorageDirectory отмечен устаревшим, но это по-прежнему путь к общей памяти
        string? shared = Android.OS.Environment.ExternalStorageDirectory?.Path;
#pragma warning restore CS0618
        if (!string.IsNullOrEmpty(shared) && Directory.Exists(shared!))
            return shared!;

        string fallback = Android.App.Application.Context.GetExternalFilesDir(null)?.Path
            ?? Android.App.Application.Context.FilesDir!.Path!;
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Нужно ли вообще разрешение: ниже Android 11 «всех файлов» не спрашивают.</summary>
    public static bool NeedsAllFilesPermission() => Build.VERSION.SdkInt >= BuildVersionCodes.R;

    /// <summary>
    /// Путь лежит в общей памяти телефона (или в папке самого приложения). Им пользуются и
    /// список файлов, и поставщик файлов для чужих приложений: за пределы этих папок выхода нет.
    /// </summary>
    public static bool IsInsideShared(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('/', '\\');
        }
        catch
        {
            return false;
        }

        foreach (string root in SharedRoots())
        {
            if (root.Length == 0)
                continue;

            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return true;

            if (full.Length > root.Length
                && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && full[root.Length] == '/')
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SharedRoots()
    {
        yield return StartDirectory().TrimEnd('/', '\\');

        string? own = Android.App.Application.Context.GetExternalFilesDir(null)?.Path;
        yield return own?.TrimEnd('/', '\\') ?? "";

        string? internalDir = Android.App.Application.Context.FilesDir?.Path;
        yield return internalDir?.TrimEnd('/', '\\') ?? "";

        // В кэше лежат временные файлы: их приложение извлекает из контейнера, чтобы открыть
        // чужой программой.
        string? cache = Android.App.Application.Context.CacheDir?.Path;
        yield return cache?.TrimEnd('/', '\\') ?? "";
    }

    public static bool HasAllFilesAccess() =>
        !NeedsAllFilesPermission() || Android.OS.Environment.IsExternalStorageManager;

    /// <summary>Сколько свободно байт на разделе, где лежит path.</summary>
    public static long AvailableBytes(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? probe = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe))
                probe = "/";

            var stat = new StatFs(probe!);
            return stat.AvailableBytes;
        }
        catch
        {
            return -1; // не удалось измерить — проверка не должна блокировать работу
        }
    }

    /// <summary>Экран настроек с переключателем «Доступ ко всем файлам» (Android 11+).</summary>
    public static Intent AllFilesAccessSettingsIntent()
    {
        var intent = new Intent("android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION");
        try
        {
            intent.SetData(Android.Net.Uri.Parse("package:" + Android.App.Application.Context.PackageName));
        }
        catch
        {
            // Не все оболочки принимают package-данные: экран откроется и без них.
        }
        return intent;
    }
}
