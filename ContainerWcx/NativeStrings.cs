using System.Runtime.InteropServices;
using System.Text;

namespace ContainerWcx;

/// <summary>
/// Чтение и запись нативных строк: UTF-16 для W-функций и ANSI (кодовая страница системы) для
/// функций «первого издания». ANSI converts via kernel32 (CP_ACP=0): NativeAOT не тянет таблицу
/// кодовых страниц System.Text, а Win32 уже делает ровно то преобразование, которое ожидает TC.
/// Всё на unsafe-указателях: аллокаций минимум, потому что вызовов ReadHeaderExW много.
/// </summary>
internal static class NativeStrings
{
    private const uint CP_ACP = 0;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static unsafe extern int MultiByteToWideChar(
        uint codePage, uint flags, byte* multiByte, int byteCount, char* wideChar, int wideChars);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static unsafe extern int WideCharToMultiByte(
        uint codePage, uint flags, char* wideChar, int wideChars, byte* multiByte, int byteCount,
        byte* defaultChar, byte* usedDefaultChar);

    /// <summary>WCHAR* → string. null → пустая строка (TC передаёт NULL для «нет пути»).</summary>
    public static unsafe string ReadZ(char* p)
    {
        if (p == null)
            return string.Empty;

        int length = 0;
        while (p[length] != '\0')
            length++;

        return new string(p, 0, length);
    }

    /// <summary>
    /// Запись строки в fixed-буфер TC с обрезкой по ёмкости буфера и обязательным terminating null.
    /// bufferCapacity — число символов, не байт.
    /// </summary>
    public static unsafe void WriteZ(char* buffer, int bufferCapacity, string value)
    {
        if (buffer == null || bufferCapacity <= 0)
            return;

        int take = Math.Min(value.Length, bufferCapacity - 1);
        for (int i = 0; i < take; i++)
            buffer[i] = value[i];

        for (int i = take; i < bufferCapacity; i++)
            buffer[i] = '\0';
    }

    /// <summary>char* (ANSI, CP_ACP) → string. null → пустая строка.</summary>
    public static unsafe string ReadZAnsi(byte* p)
    {
        if (p == null)
            return string.Empty;

        int length = 0;
        while (p[length] != 0)
            length++;

        if (length == 0)
            return string.Empty;

        int needed = MultiByteToWideChar(CP_ACP, 0, p, length, null, 0);
        if (needed <= 0)
            return RawChars(length, p);

        var buffer = new char[needed];
        fixed (char* wide = buffer)
        {
            int written = MultiByteToWideChar(CP_ACP, 0, p, length, wide, needed);
            return written <= 0 ? RawChars(length, p) : new string(wide, 0, written);
        }
    }

    /// <summary>Невыводимая последовательность: сохраняем байты как есть, имя хотя бы не исчезнет.</summary>
    private static unsafe string RawChars(int length, byte* p)
    {
        var fallback = new char[length];
        for (int i = 0; i < length; i++)
            fallback[i] = (char)p[i];
        return new string(fallback);
    }

    /// <summary>string → байты CP_ACP (для обратного вызова tProcessDataProc в ANSI-ветке).</summary>
    public static unsafe byte[] AnsiBytes(string value)
    {
        fixed (char* wide = value)
        {
            int needed = WideCharToMultiByte(CP_ACP, 0, wide, value.Length, null, 0, null, null);
            if (needed <= 0)
                return Array.Empty<byte>();

            var buffer = new byte[needed];
            fixed (byte* ansi = buffer)
            {
                int written = WideCharToMultiByte(CP_ACP, 0, wide, value.Length, ansi, needed, null, null);
                if (written <= 0)
                    return Array.Empty<byte>();

                if (written == buffer.Length)
                    return buffer;

                var trimmed = new byte[written];
                Array.Copy(buffer, trimmed, written);
                return trimmed;
            }
        }
    }

    /// <summary>
    /// Запись строки в fixed-буфер TC как ANSI с обрезкой по ёмкости и обнулением остатка буфера.
    /// bufferCapacity — число байт.
    /// </summary>
    public static unsafe void WriteZAnsi(byte* buffer, int bufferCapacity, string value)
    {
        if (buffer == null || bufferCapacity <= 0)
            return;

        byte[] bytes = AnsiBytes(value);
        int take = Math.Min(bytes.Length, bufferCapacity - 1);

        for (int i = 0; i < take; i++)
            buffer[i] = bytes[i];

        for (int i = take; i < bufferCapacity; i++)
            buffer[i] = 0;
    }

    /// <summary>
    /// Список имён TC (AddList у PackFiles, DeleteList у DeleteFiles): строки идут одна за другой,
    /// каждая закончена нулём, а весь список — дополнительным нулём, то есть два нуля подряд в конце.
    /// null или сразу два нуля = пустой список.
    /// </summary>
    public static unsafe List<string> ReadZList(char* p)
    {
        var names = new List<string>();
        if (p == null)
            return names;

        int offset = 0;
        while (p[offset] != '\0' && names.Count < MaxListEntries)
        {
            int start = offset;
            while (p[offset] != '\0')
                offset++;

            names.Add(new string(p, start, offset - start));
            offset++; // разделитель: дальше либо следующая строка, либо terminator списка
        }

        return names;
    }

    /// <summary>ANSI-ветка того же списка: каждое имя преобразуется из кодовой страницы системы.</summary>
    public static unsafe List<string> ReadZListAnsi(byte* p)
    {
        var names = new List<string>();
        if (p == null)
            return names;

        int offset = 0;
        while (p[offset] != 0 && names.Count < MaxListEntries)
        {
            int end = offset;
            while (p[end] != 0)
                end++;

            names.Add(ReadZAnsi(p + offset));
            offset = end + 1;
        }

        return names;
    }

    /// <summary>
    /// Страховка от испорченного списка: если terminator забыт, чтение ушло бы за границу блока.
    /// Столько файлов TC через панель не передаёт, поэтому предел не мешает реальной работе.
    /// </summary>
    private const int MaxListEntries = 1_000_000;

    /// <summary>
    /// tHeaderDataExW.FileTime — упакованное MS-DOS время. Год вне 1980..2100 не выразим:
    /// берём середину диапазона, чтобы TC показал дату, а не мусор или «1980».
    /// </summary>
    public static int ToDosTime(DateTime value)    {
        int year = Math.Clamp(value.Year, 1980, 2100);
        int month = Math.Clamp(value.Month, 1, 12);
        int day = Math.Clamp(value.Day, 1, 31);

        return ((year - 1980) << 25) | (month << 21) | (day << 16)
             | (value.Hour << 11) | (value.Minute << 5) | (value.Second / 2);
    }
}
