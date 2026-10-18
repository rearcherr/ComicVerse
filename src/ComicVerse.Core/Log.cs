using System.Globalization;
using System.IO;

namespace ComicVerse.Core;

public static class Log
{
    private static readonly object Lock = new();
    private static string? _dir;
    private static bool _dirReady;
    private static string? _lastFailure;

    /// <summary>单个日志文件上限；超过则滚动到 .1/.2/... 。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>保留的历史日志份数（app.1.log … app.N.log）。</summary>
    private const int MaxHistory = 4;

    /// <summary>
    /// 配置日志目录。目录创建失败时不会抛出：日志系统退化为只在 stderr 输出，
    /// 由调用方（启动流程）自行决定是否提示用户，绝不因日志不可写而阻断启动。
    /// </summary>
    public static void Configure(string logDir)
    {
        lock (Lock)
        {
            _dir = logDir;
            _dirReady = false;
            try
            {
                Directory.CreateDirectory(logDir);
                _dirReady = true;
            }
            catch (Exception ex)
            {
                // 目录不可写（只读介质 / 权限不足 / 磁盘满）：保留 _dir 但标记不可用，
                // 后续 Write 会退到 stderr，而不是让整个启动流程崩溃。
                _dirReady = false;
                EmitToStderr("日志目录创建失败，日志将只输出到标准错误", ex);
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message} | {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (Lock)
        {
            // 未配置日志目录（例如单元测试或库调用方直接使用 Log）：保持静默，不刷 stderr。
            // 只有「已配置但写不进去」才走兜底通道，避免正常日志污染测试输出。
            if (_dir is null) return;
            try
            {
                if (!_dirReady)
                {
                    Directory.CreateDirectory(_dir);
                    _dirReady = true;
                }
                string file = Path.Combine(_dir, "app.log");
                File.AppendAllText(file, line);
                if (new FileInfo(file).Length > MaxBytes)
                    RotateLocked(_dir, line);
            }
            catch (Exception ex)
            {
                // 写失败（磁盘满 / 文件被占用 / 权限）：退到 stderr，并只在内容变化时输出，避免刷屏。
                _dirReady = false;
                EmitToStderr(line.TrimEnd(), ex);
            }
        }
    }

    /// <summary>
    /// 真正的滚动轮转：app.4.log 删除，app.3→app.4，…，app.1→app.2，app.log→app.1.log，
    /// 然后把触发轮转的这一行写入新的 app.log。
    /// 旧实现用 File.WriteAllText(file, line) 直接把 2MB 历史覆盖成一行，崩溃前的上下文全丢。
    /// </summary>
    private static void RotateLocked(string dir, string firstLineOfNewLog)
    {
        string file = Path.Combine(dir, "app.log");
        try
        {
            // app.N.log 最老，先删
            string oldest = Path.Combine(dir, $"app.{MaxHistory}.log");
            if (File.Exists(oldest))
                File.Delete(oldest);

            for (int i = MaxHistory - 1; i >= 1; i--)
            {
                string src = Path.Combine(dir, $"app.{i}.log");
                if (!File.Exists(src)) continue;
                File.Move(src, Path.Combine(dir, $"app.{i + 1}.log"), overwrite: true);
            }

            File.Move(file, Path.Combine(dir, "app.1.log"), overwrite: true);
        }
        catch (Exception ex)
        {
            // 滚动失败不能丢后续日志：退化为截断当前文件继续写，并记录原因。
            EmitToStderr("日志轮转失败，将截断 app.log 继续写", ex);
            try { File.WriteAllText(file, firstLineOfNewLog); return; } catch { }
        }

        try
        {
            File.WriteAllText(file, firstLineOfNewLog);
        }
        catch (Exception ex)
        {
            EmitToStderr("轮转后写入新日志失败", ex);
        }
    }

    /// <summary>日志写不进去时的最后通道：标准错误 + 调试输出。同一异常只报一次，避免高频刷屏。</summary>
    private static void EmitToStderr(string message, Exception? ex)
    {
        string text = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        if (_lastFailure == text) return;
        _lastFailure = text;
        try { Console.Error.WriteLine("[ComicVerse.Log] " + text); } catch { }
        try { System.Diagnostics.Debug.WriteLine("[ComicVerse.Log] " + text); } catch { }
    }
}
