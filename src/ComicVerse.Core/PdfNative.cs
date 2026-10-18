using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ComicVerse.Core;

/// <summary>
/// 确保 pdfium 原生库可被加载。
///
/// 安全设计（对应审计报告 A-2）：
/// 1) 提取目录固定在 <c>%LOCALAPPDATA%\ComicVerse\native</c>（每用户私有），
///    不再使用对任何本地进程可写的 <c>%TEMP%</c>；调用方传入的目录不再被信任。
/// 2) 每次启动都与嵌入资源的 SHA-256 比对，不一致则以「写 .tmp → File.Move(overwrite:true)」
///    原子覆盖；不再有「文件已存在就直接加载」的 TOCTOU 窗口。
/// 3) 通过 <see cref="NativeLibrary.SetDllImportResolver"/> 以**绝对路径**优先加载，
///    <c>SetDllDirectoryW</c> 仅在绝对路径加载失败时作为兜底。
/// </summary>
public static class PdfNative
{
    private const string ResourceName = "pdfium.dll";
    private const string FileName = "pdfium.dll";

    private static int _done;

    private static string? _resolvedPath;
    private static bool _resolverInstalled;

    private static readonly object InitLock = new();

    /// <summary>私有提取目录：%LOCALAPPDATA%\ComicVerse\native。</summary>
    public static string DefaultExtractDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ComicVerse",
            "native");

    /// <summary>
    /// 确保 pdfium 可用。<paramref name="preferredDir"/> 仅为兼容旧调用签名保留：
    /// 若它不位于本程序私有数据目录下则被忽略（防止再被引导到 %TEMP% 等共享可写目录）。
    /// </summary>
    public static void EnsureExtracted(string? preferredDir = null)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;

        lock (InitLock)
        {
            try
            {
                InstallResolver();

                // 1) 常规发布 / dotnet run：原生库已随应用分发，直接用 baseDir 的绝对路径。
                string? alongside = FindAlongsideApp();
                if (alongside is not null)
                {
                    _resolvedPath = alongside;
                    Log.Info($"pdfium 使用随应用分发的副本：{alongside}");
                    return;
                }

                // 2) 单文件发布：从嵌入资源提取到私有目录并校验。
                string dir = ResolveExtractDir(preferredDir);
                string? extracted = ExtractVerified(dir);
                if (extracted is null)
                {
                    Log.Error("嵌入的 pdfium.dll 资源不存在，PDF 功能将不可用");
                    return;
                }
                _resolvedPath = extracted;
                Log.Info($"pdfium 已就绪：{extracted}");
            }
            catch (Exception ex)
            {
                Log.Error("准备 pdfium 失败", ex);
            }
        }
    }

    /// <summary>当前解析到的 pdfium 绝对路径；未初始化/失败时为 null（用于自检）。</summary>
    public static string? ResolvedPath
    {
        get { lock (InitLock) return _resolvedPath; }
    }

    private static string? FindAlongsideApp()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string candidate in new[]
                 {
                     Path.Combine(baseDir, FileName),
                     Path.Combine(baseDir, "runtimes", "win-x64", "native", FileName),
                     Path.Combine(baseDir, "x64", FileName),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// 决定提取目录：默认用 <see cref="DefaultExtractDir"/>；
    /// 只有当调用方给出的目录位于本用户 LocalAppData 下时才采纳，否则忽略。
    /// </summary>
    private static string ResolveExtractDir(string? preferredDir)
    {
        if (!string.IsNullOrWhiteSpace(preferredDir))
        {
            try
            {
                string full = Path.GetFullPath(preferredDir);
                string localAppData = Path.GetFullPath(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (full.StartsWith(localAppData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return full;
                Log.Error($"忽略不安全的 pdfium 提取目录（不在 LocalAppData 下）：{full}，改用私有目录");
            }
            catch (Exception ex)
            {
                Log.Error("解析 pdfium 提取目录失败，改用私有目录", ex);
            }
        }
        return DefaultExtractDir;
    }

    /// <summary>
    /// 把嵌入的 pdfium 提取到 dir 并保证其内容与嵌入资源一致（SHA-256 比对）。
    /// 返回绝对路径；资源缺失时返回 null。
    /// </summary>
    private static string? ExtractVerified(string dir)
    {
        using Stream? res = typeof(PdfNative).Assembly.GetManifestResourceStream(ResourceName);
        if (res is null) return null;

        byte[] payload;
        using (var buffer = new MemoryStream((int)Math.Min(res.Length, 64L * 1024 * 1024)))
        {
            res.CopyTo(buffer);
            payload = buffer.ToArray();
        }
        byte[] expected = SHA256.HashData(payload);

        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, FileName);

        if (IsHashMatch(target, expected))
            return target;

        // 原子替换：先写同目录 .tmp，再一次性 Move 覆盖，避免半截文件被加载。
        string tmp = Path.Combine(dir, FileName + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }

            // 已存在但内容不符（可能是被预置/损坏的旧副本）→ 强制覆盖。
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            // 目标可能正被其他进程占用；若磁盘上的副本哈希其实是对的，仍然可用。
            if (IsHashMatch(target, expected))
            {
                TryDelete(tmp);
                Log.Error("pdfium 覆盖写入失败，但现有副本哈希一致，继续使用", ex);
                return target;
            }
            TryDelete(tmp);
            throw;
        }

        if (!IsHashMatch(target, expected))
            throw new InvalidDataException("pdfium.dll 提取后哈希校验失败");

        return target;
    }

    private static bool IsHashMatch(string path, byte[] expected)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // 注意：expected 是 32 字节的 SHA-256 摘要，不能拿它和 fs.Length 比大小
            // （早期版本在这里比较，导致哈希永远不匹配、每次都重新提取）。
            byte[] actual = SHA256.HashData(fs);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 安装 DllImport 解析器：对 "pdfium.dll" 优先返回已知的绝对路径；
    /// 绝对路径加载失败时再用 SetDllDirectoryW 兜底（只在必要时）。
    /// </summary>
    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PdfNative).Assembly, (name, asm, searchPath) =>
            {
                if (!string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                string? known = _resolvedPath;
                if (!string.IsNullOrEmpty(known) && NativeLibrary.TryLoad(known, out IntPtr h1))
                    return h1;

                // 兜底：把私有目录加入 DLL 搜索路径后按名字加载。
                string dir = known is not null ? Path.GetDirectoryName(known)! : DefaultExtractDir;
                if (Directory.Exists(dir))
                {
                    SetDllDirectoryW(dir);
                    if (NativeLibrary.TryLoad(Path.Combine(dir, FileName), out IntPtr h2))
                        return h2;
                }
                return IntPtr.Zero; // 交给默认解析
            });
            _resolverInstalled = true;
        }
        catch (Exception ex)
        {
            Log.Error("安装 pdfium DllImport 解析器失败，将回退到默认搜索", ex);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);
}
