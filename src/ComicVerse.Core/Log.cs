using System.IO;

namespace ComicVerse.Core;

public static class Log
{
    private static readonly object Lock = new();
    private static string? _dir;

    public static void Configure(string logDir)
    {
        _dir = logDir;
        Directory.CreateDirectory(logDir);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message} | {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            if (_dir is null) return;
            lock (Lock)
            {
                string file = Path.Combine(_dir, "app.log");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(file, line);
                var fi = new FileInfo(file);
                if (fi.Length > 2 * 1024 * 1024)
                {
                    File.WriteAllText(file, line);
                }
            }
        }
        catch
        {
            // 日志失败不应影响主流程
        }
    }
}
