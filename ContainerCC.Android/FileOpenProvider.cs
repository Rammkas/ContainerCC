using Android.Content;
using Android.OS;

namespace ContainerCC.Mobile;

/// <summary>
/// Отдаёт файлы общей памяти другим приложениям по адресу content://…
///
/// Android с 7-й версии не разрешает передавать чужому приложению адрес file://… — вместо него
/// нужен адрес content://, который выдаёт такой поставщик. Отдаём файл только на чтение и только
/// тот, что лежит в общей памяти телефона: чужое приложение не получит ни доступа на запись, ни
/// доступа к служебным папкам. Поставщик не выгружен наружу (exported = false): им пользуются
/// только те, кому мы выдали на время разрешение через адрес.
/// </summary>
[ContentProvider(new[] { AuthorityName }, Exported = false, GrantUriPermissions = true)]
public class FileOpenProvider : ContentProvider
{
    public const string AuthorityName = "com.containercc.files";

    /// <summary>Адрес файла для чужого приложения: content://com.containercc.files/storage/emulated/0/…</summary>
    public static Android.Net.Uri ContentUriFor(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string relative = string.Join("/", parts.Select(part => Android.Net.Uri.Encode(part)));

        return Android.Net.Uri.Parse("content://" + AuthorityName + "/" + relative)!;
    }

    public override bool OnCreate() => true;

    /// <summary>Чужое приложение открывает файл: всегда только на чтение.</summary>
    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri? uri, string? mode)
    {
        string? path = LocalPathOf(uri);
        if (path is null || !StorageAccess.IsInsideShared(path))
            return null;

        try
        {
            return ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Тип файла по расширению: по нему Android подбирает приложение.</summary>
    public override string? GetType(Android.Net.Uri? uri)
    {
        string? path = LocalPathOf(uri);
        if (path is null)
            return "application/octet-stream";

        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        return extension switch
        {
            "txt" or "md" or "log" => "text/plain",
            "csv" => "text/comma-separated-values",
            "json" or "xml" => "text/plain",
            "pdf" => "application/pdf",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ppt" => "application/vnd.ms-powerpoint",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "zip" => "application/zip",
            "rar" => "application/vnd.rar",
            "7z" => "application/x-7z-compressed",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "heic" => "image/heic",
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "ogg" => "audio/ogg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "mp4" => "video/mp4",
            "m4v" or "mov" => "video/quicktime",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "epub" => "application/epub+zip",
            "apk" => "application/vnd.android.package-archive",
            _ => "application/octet-stream",
        };
    }

    /// <summary>Из адреса восстанавливается обычный путь файла на телефоне.</summary>
    private static string? LocalPathOf(Android.Net.Uri? uri)
    {
        if (uri is null)
            return null;

        IList<string>? parts = uri.PathSegments;
        if (parts is null || parts.Count == 0)
            return null;

        return "/" + string.Join("/", parts);
    }

    // Кроме выдачи файлов поставщик ничего не умеет: ни списка, ни изменения.

    public override Android.Database.ICursor? Query(Android.Net.Uri? uri, string[]? projection, string? selection,
        string[]? selectionArgs, string? sortOrder) => null;

    public override Android.Net.Uri? Insert(Android.Net.Uri? uri, ContentValues? values) => null;

    public override int Update(Android.Net.Uri? uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;

    public override int Delete(Android.Net.Uri? uri, string? selection, string[]? selectionArgs) => 0;
}
