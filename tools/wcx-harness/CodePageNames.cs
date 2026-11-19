using System.Runtime.InteropServices;

/// <summary>
/// Имена файлов для проходов, которые идут через ANSI-ветку плагина. ANSI-ветка имитирует
/// Win32-хост Total Commander: хост отдаёт имена в кодовой странице системы (CP_ACP), а
/// непредставимые символы Win32 уже заменил знаком «?». Поэтому имя, которое харнесс или
/// тестовая обёртка создаёт на диске, обязано быть представимым в текущей кодовой странице —
/// иначе плагин получит «?» и честно откажется, и проверка станет проверкой кодовой страницы
/// машины, а не плагина (на dev-машине CP_ACP = 1251, в CI Windows — 1252).
///
/// Pick() берёт первый кандидат, который кодировка системы умеет записать без замены:
/// кириллица проходит на 1251, латиница с диакритикой — на 1252. Порядок кандидатов важен:
/// на машине с CP_ACP = 1251 поведение остаётся ровно прежним (выбирается кириллица).
/// Диакритика выбрана не случайна: имена должны остаться не-ASCII и на 1252, иначе проверки
/// «в контейнере есть не-ASCII имена» потеряли бы смысл в CI.
///
/// Файл общий: харнесс компилирует его из своей папки, xUnit-обёртка подключает через
/// Compile Include — правило выбора имени обязано быть одним и тем же в обоих местах.
/// Только безопасные P/Invoke (без unsafe): тестовый проект собран без AllowUnsafeBlocks.
/// </summary>
internal static class CodePageNames
{
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, string wideChar, int wideChars, byte[]? multiByte, int byteCount,
        IntPtr defaultChar, ref int usedDefaultChar);

    /// <summary>Кодовая страница системы (CP_ACP): 1251 на русской машине, 1252 на CI-машине.</summary>
    public static uint CurrentCodePage() => GetACP();

    /// <summary>Имя записывается в CP_ACP без замены символов на «?» (проверка через lpUsedDefaultChar).</summary>
    public static bool IsRepresentable(string name)
    {
        int needed = WideCharToMultiByte(0, 0, name, name.Length, null, 0, IntPtr.Zero, ref UnusedFlag);
        if (needed <= 0)
            return false;

        byte[] buffer = new byte[needed];
        int usedDefault = 0;
        int written = WideCharToMultiByte(0, 0, name, name.Length, buffer, buffer.Length, IntPtr.Zero, ref usedDefault);
        return written == needed && usedDefault == 0;
    }

    /// <summary>Первый кандидат, который текущая кодовая страница системы записывает без замены.</summary>
    public static string Pick(params string[] candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Length == 0)
            throw new ArgumentException("Нужен хотя бы один кандидат имени", nameof(candidates));

        foreach (string candidate in candidates)
            if (IsRepresentable(candidate))
                return candidate;

        // Последний кандидат обязан быть ASCII — тогда он представим в любой ANSI-странице.
        return candidates[^1];
    }

    private static int UnusedFlag;
}
