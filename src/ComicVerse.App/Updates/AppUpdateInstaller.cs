using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Updates;

namespace ComicVerse.App.Updates;

internal static class AppUpdateInstaller
{
    internal const string ApplyArgument = "--comicverse-apply-update";
    internal const string RestartArgument = "--comicverse-update-restart";
    private const string StagePrefix = ".ComicVerse-update-";
    internal const string CandidateName = "candidate.exe";
    private const string HelperName = "ComicVerse.Updater.exe";
    private const string BackupName = "previous.exe";
    private const string ReadyName = "ready.txt";
    private const string HelperReadyName = "helper-ready.txt";
    private const string CommitName = "proceed.txt";
    private static bool IsSmokeProcess => Environment.GetCommandLineArgs().Contains("--smoke");

    internal static string CurrentExecutable()
        => Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 ComicVerse 程序位置。");

    internal static string CreateStage(string target)
    {
        if (!string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.ChangeExtension(target, ".runtimeconfig.json")))
            throw new InvalidOperationException("请使用 GitHub 发布的 Windows 免安装版来在线更新。");
        RequireRegularPath(target);
        string directory = Path.GetDirectoryName(target)!;
        RequireRegularPath(directory);
        string stage = Path.Combine(directory, StagePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        return stage;
    }

    internal static bool IsStageFor(string stage, string target)
    {
        string name = Path.GetFileName(stage);
        return name.StartsWith(StagePrefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[StagePrefix.Length..], "N", out _) &&
               string.Equals(Path.GetDirectoryName(stage), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase);
    }

    internal static void Prepare(DownloadedUpdate update, string target)
    {
        string stage = GetStage(update, target);
        ValidateVersion(update.FilePath, update.Release.Version, target);
        File.Copy(target, Path.Combine(stage, HelperName), overwrite: false);
    }

    internal static async Task StartHelperAsync(DownloadedUpdate update, string target)
    {
        string stage = GetStage(update, target);
        using var current = Process.GetCurrentProcess();
        using var helper = StartProcess(Path.Combine(stage, HelperName), Path.GetDirectoryName(target)!,
            ApplyArgument, current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(),
            Path.GetFileName(target), update.Release.Version.ToString(3), update.Sha256);
        try
        {
            string ready = Path.Combine(stage, HelperReadyName);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15) && !helper.HasExited)
            {
                if (HasMarker(ready, helper.Id))
                {
                    WriteMarker(Path.Combine(stage, CommitName), current.Id);
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new IOException("更新助手未能启动，请稍后重试或手动下载新版。");
        }
        catch
        {
            // This process was launched by us and has no permission to replace
            // the original until the commit marker exists.
            if (!helper.HasExited)
            {
                try { helper.Kill(); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("关闭未就绪的更新助手失败", ex); }
            }
            throw;
        }
    }

    private static string GetStage(DownloadedUpdate update, string target)
    {
        string stage = Path.GetDirectoryName(Path.GetFullPath(update.FilePath))!;
        if (!IsStageFor(stage, target) || Path.GetFileName(update.FilePath) != CandidateName)
            throw new InvalidDataException("更新文件不在程序的暂存目录中。");
        RequireRegularPath(stage);
        return stage;
    }

    internal static async Task ApplyAsync(string[] args)
    {
        if (args.Length == 7 && args[^1] == "--smoke") args = args[..^1];
        string helper = CurrentExecutable();
        string stage = Path.GetDirectoryName(helper)!;
        if (args.Length != 6 || args[0] != ApplyArgument || Path.GetFileName(helper) != HelperName ||
            !int.TryParse(args[1], out int parentPid) || parentPid < 1 ||
            !long.TryParse(args[2], out long parentStarted) || parentStarted < 1 ||
            args[3] != Path.GetFileName(args[3]) || args[3].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(args[3]), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(args[4], out var version) || version.Build < 0 || version.Revision != -1 ||
            args[5].Length != 64 || !args[5].All(Uri.IsHexDigit))
            throw new InvalidDataException("更新交接参数无效。");

        string directory = Path.GetDirectoryName(stage)!;
        string target = Path.Combine(directory, args[3]);
        if (!IsStageFor(stage, target)) throw new InvalidDataException("更新暂存目录无效。");
        RequireRegularPath(stage);
        RequireRegularPath(directory);
        RequireRegularPath(helper);
        RequireRegularPath(target);
        string candidate = Path.Combine(stage, CandidateName);
        string backup = Path.Combine(stage, BackupName);

        // One helper may act on this installation at a time. The file is closed
        // and removed by Windows even if the assistant process is stopped.
        string lockPath = Path.Combine(directory, ".ComicVerse-update.lock");
        if (File.Exists(lockPath)) RequireRegularPath(lockPath);
        await using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var parent = Process.GetProcessById(parentPid);
        if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != parentStarted ||
            !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新请求的进程不匹配。");

        WriteMarker(Path.Combine(stage, HelperReadyName), Environment.ProcessId);
        while (!HasMarker(Path.Combine(stage, CommitName), parentPid))
        {
            if (parent.HasExited) return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        RequireNoOtherInstance(target);

        string oldHash = await HashAsync(helper, timeout.Token).ConfigureAwait(false);
        if (!string.Equals(await HashAsync(target, timeout.Token).ConfigureAwait(false), oldHash, StringComparison.Ordinal))
            throw new IOException("程序在更新准备期间已改变，请重新检查更新。");

        bool restartIssued = false;
        try
        {
            RequireRegularPath(candidate);
            ValidateVersion(candidate, version, target);
            if (!string.Equals(await HashAsync(candidate, timeout.Token).ConfigureAwait(false), args[5], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件校验失败。");
            if (File.Exists(backup)) throw new IOException("暂存目录中已有程序备份。");
            File.Replace(candidate, target, backup);
            if (!string.Equals(await HashAsync(target, timeout.Token).ConfigureAwait(false), args[5], StringComparison.OrdinalIgnoreCase))
                throw new IOException("替换后的程序校验失败。");

            using var next = StartRestart(target, stage, "ready");
            restartIssued = true;
            if (await WaitForReadyAsync(next, stage).ConfigureAwait(false)) return;
            // A process that is still running may already be using the library.
            // Preserve the backup and report the unconfirmed startup in that case.
            if (!next.HasExited)
                throw new InvalidOperationException($"新版启动尚未确认，旧版备份保存在：\n{backup}");
            restartIssued = false;
            throw new InvalidOperationException("新版未能启动到书架。");
        }
        catch (Exception ex) when (!restartIssued)
        {
            Log.Error("应用更新失败，尝试恢复旧版", ex);
            try
            {
                await RestoreOriginalAsync(target, backup, oldHash, args[5]).ConfigureAwait(false);
                using var restored = StartRestart(target, stage, "restored");
            }
            catch (Exception recoveryError)
            {
                throw new IOException($"未能重新打开 ComicVerse。旧版备份（若已生成）：\n{backup}", recoveryError);
            }
        }
    }

    internal static void RequireNoOtherInstance(string target)
    {
        var instances = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target));
        try
        {
            foreach (var instance in instances)
            {
                if (instance.Id == Environment.ProcessId || instance.HasExited) continue;
                bool samePath;
                try { samePath = string.Equals(instance.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase); }
                catch (Exception) when (instance.HasExited) { continue; }
                if (samePath) throw new IOException("请先关闭从同一位置启动的其它 ComicVerse 窗口，再重试更新。");
            }
        }
        finally { foreach (var instance in instances) instance.Dispose(); }
    }

    private static async Task<bool> WaitForReadyAsync(Process next, string stage)
    {
        string ready = Path.Combine(stage, ReadyName);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(25) && !next.HasExited)
        {
            if (HasMarker(ready, next.Id)) return true;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task RestoreOriginalAsync(string target, string backup, string oldHash, string newHash)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (File.Exists(target))
        {
            RequireRegularPath(target);
            string actual = await HashAsync(target, timeout.Token).ConfigureAwait(false);
            if (string.Equals(actual, oldHash, StringComparison.Ordinal)) return;
            if (!string.Equals(actual, newHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("程序文件已被其它操作修改，无法安全恢复旧版。");
        }
        RequireRegularPath(backup);
        if (!string.Equals(await HashAsync(backup, timeout.Token).ConfigureAwait(false), oldHash, StringComparison.Ordinal))
            throw new InvalidDataException("旧版备份校验失败。");
        File.Move(backup, target, overwrite: true);
    }

    internal static void CompleteRestart(string[] args, Window owner)
    {
        if (args.Length == 5 && args[^1] == "--smoke") args = args[..^1];
        if (args.Length != 4 || args[0] != RestartArgument || args[1] != Path.GetFileName(args[1]) ||
            !int.TryParse(args[2], out int helperPid) || helperPid < 1 || args[3] is not ("ready" or "restored")) return;
        string target = CurrentExecutable();
        string stage = Path.Combine(Path.GetDirectoryName(target)!, args[1]);
        if (!IsStageFor(stage, target) || !Directory.Exists(stage)) return;
        RequireRegularPath(stage);
        if (args[3] == "ready") WriteMarker(Path.Combine(stage, ReadyName), Environment.ProcessId);
        _ = CleanAfterHelperAsync(stage, helperPid);
        if (IsSmokeProcess && Environment.GetEnvironmentVariable("SMOKE_OUT_DIR") is { Length: > 0 } evidence)
        {
            var data = new
            {
                Environment.ProcessId, Environment = App.GetSmokeProcessEnvironment(),
                Executable = target, Version = App.AppVersion, DataDir = App.AppDataDir, Result = args[3],
                Books = App.Library.GetBooks().Select(b => new { b.Id, b.FilePath, b.Progress, ReadingPosition = App.Library.GetProgress(b.Id) }).ToArray()
            };
            File.WriteAllText(Path.Combine(evidence, "online-update-restarted.json"), System.Text.Json.JsonSerializer.Serialize(data));
        }
        if (args[3] == "restored" && !IsSmokeProcess)
            MessageBox.Show(owner, "更新未能完成。已恢复并启动原版本，请稍后重试。", "ComicVerse 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static async Task CleanAfterHelperAsync(string stage, int helperPid)
    {
        try
        {
            if (helperPid == Environment.ProcessId) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                using var helper = Process.GetProcessById(helperPid);
                if (!helper.HasExited && !string.Equals(helper.MainModule?.FileName, Path.Combine(stage, HelperName),
                        StringComparison.OrdinalIgnoreCase)) return;
                await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (helper.ExitCode != 0) return;
            }
            catch (ArgumentException) { }
            CleanStage(stage);
        }
        catch (Exception ex) { Log.Error("清理更新临时文件失败", ex); }
    }

    internal static void CleanStage(string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return;
            RequireRegularPath(stage);
            foreach (string name in new[] { CandidateName, HelperName, BackupName, ReadyName, HelperReadyName, CommitName })
            {
                File.Delete(Path.Combine(stage, name));
                File.Delete(Path.Combine(stage, name + ".tmp"));
            }
            Directory.Delete(stage);
        }
        catch (Exception ex) { Log.Error("清理更新暂存目录失败", ex); }
    }

    private static bool HasMarker(string path, int pid)
        => File.Exists(path) && string.Equals(File.ReadAllText(path), pid.ToString(), StringComparison.Ordinal);

    private static void WriteMarker(string path, int pid)
    {
        string temporary = path + ".tmp";
        using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            writer.Write(pid);
        File.Move(temporary, path);
    }

    private static void RequireRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("在线更新不能写入链接目录或链接文件，请手动下载新版。");
    }

    private static void ValidateVersion(string candidate, Version expected, string target)
    {
        RequireRegularPath(candidate);
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(candidate).ProductVersion, out var found) ||
            !Version.TryParse(FileVersionInfo.GetVersionInfo(target).ProductVersion, out var old) ||
            found != expected || found <= old)
            throw new InvalidDataException("更新文件的版本与正式发布记录不符。");
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static Process StartRestart(string target, string stage, string result)
        => StartProcess(target, Path.GetDirectoryName(target)!, RestartArgument, Path.GetFileName(stage),
            Environment.ProcessId.ToString(), result);

    private static Process StartProcess(string path, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = directory };
        if (Path.IsPathFullyQualified(App.AppDataDir)) start.Environment["COMICVERSE_DATA_DIR"] = App.AppDataDir;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (IsSmokeProcess) start.ArgumentList.Add("--smoke");
        return Process.Start(start) ?? throw new IOException("无法启动 ComicVerse 更新进程。");
    }
}
