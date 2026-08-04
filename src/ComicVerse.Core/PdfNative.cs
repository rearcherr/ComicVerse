using System.IO;
using System.Runtime.InteropServices;

namespace ComicVerse.Core;

/// <summary>
/// 确保 PdfiumViewer 使用的原生 pdfium.dll 可被 LoadLibrary 找到。
/// 单文件发布时 DLL 被嵌入程序集，运行前需要提取到可写目录并加入 DLL 搜索路径。
/// </summary>
public static class PdfNative
{
    private static int _done;

    public static void EnsureExtracted(string fallbackDir)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "pdfium.dll")) ||
                File.Exists(Path.Combine(baseDir, "x64", "pdfium.dll")))
            {
                return; // 普通发布：DLL 已随应用分发
            }

            Directory.CreateDirectory(fallbackDir);
            string target = Path.Combine(fallbackDir, "pdfium.dll");
            if (!File.Exists(target))
            {
                using var res = typeof(PdfNative).Assembly.GetManifestResourceStream("pdfium.dll");
                if (res is null)
                {
                    Log.Error("嵌入的 pdfium.dll 资源不存在，PDF 功能将不可用");
                    return;
                }
                using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                res.CopyTo(fs);
            }

            if (!SetDllDirectoryW(fallbackDir))
                Log.Error("SetDllDirectoryW 失败，错误码 " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Error("提取 pdfium.dll 失败", ex);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string path);
}
