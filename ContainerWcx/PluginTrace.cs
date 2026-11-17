using System;
using System.IO;

namespace ContainerWcx;

/// <summary>
/// Журнал отладки плагина. Включён по умолчанию: плагин живёт в процессе TC, и без журнала нельзя
/// понять, дошёл ли TC до наших функций и почему показал ошибку. Пишется в %TEMP%\ContainerCC.wcx.log
/// (не более 256 КБ). Выключить: переменная окружения CC_WCX_TRACE=0.
/// </summary>
internal static class PluginTrace
{
    private const long MaxLogBytes = 262_144;

    private static readonly object Gate = new();
    private static readonly bool Enabled = IsEnabled();
    private static readonly string LogFile = ResolveLogFile();

    public static void Write(string message)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            try
            {
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxLogBytes)
                    File.WriteAllText(LogFile, "…журнал обрезан по размеру…\r\n");

                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\r\n");
            }
            catch (Exception)
            {
                // Журнал не обязан работать и не имеет права влиять на распаковку.
            }
        }
    }

    private static bool IsEnabled()
    {
        try
        {
            string? value = Environment.GetEnvironmentVariable("CC_WCX_TRACE");
            // По умолчанию включено: единственная запись в журнале уже отвечает на вопрос,
            // дошёл ли Total Commander до функций плагина.
            return value is null || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string ResolveLogFile()
    {
        try
        {
            string directory = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
            return Path.Combine(directory, "ContainerCC.wcx.log");
        }
        catch (Exception)
        {
            return "ContainerCC.wcx.log";
        }
    }
}
