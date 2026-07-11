using System.IO;

namespace Studio.App.Infrastructure;

/// <summary>Journal fichier minimal : logs/app-AAAAMMJJ.log, une ligne par événement.</summary>
public static class FileLog
{
    private static readonly object Gate = new();
    public static string LogsDir { get; set; } = @"D:\PhotoStudioData\logs";

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDir);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}" +
                           (ex is null ? "" : $" | {ex}");
                File.AppendAllText(
                    Path.Combine(LogsDir, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // le journal ne doit jamais faire tomber l'app
        }
    }
}
