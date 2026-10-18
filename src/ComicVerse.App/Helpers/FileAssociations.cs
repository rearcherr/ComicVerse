using Microsoft.Win32;

namespace ComicVerse.App.Helpers;

/// <summary>
/// 用户级（HKCU）文件关联注册/注销。
///
/// 审计要点（A-3）：
/// - 只写 HKEY_CURRENT_USER，无需管理员权限，不污染 HKLM。
/// - 命令模板为 <c>"exePath" "%1"</c>，两侧都有引号，路径含空格也安全。
/// - 不再注册 .zip/.rar/.tar/.7z 这类系统通用扩展名（会静默劫持其他软件的默认打开方式）。
/// - 提供 <see cref="Unregister"/>，可把被改写的扩展名恢复为系统默认。
/// </summary>
public static class FileAssociations
{
    /// <summary>与 README 声称一致的 7 个扩展名。</summary>
    public static readonly string[] Extensions =
    {
        ".cbz", ".cbr", ".cbt", ".cb7", ".pdf", ".txt", ".epub"
    };

    private const string ProgId = "ComicVerse.Document";

    /// <summary>
    /// 旧版本曾错误注册的通用扩展名。注销时一并清理，
    /// 避免老用户升级后仍被劫持。
    /// </summary>
    private static readonly string[] LegacyExtensions = { ".zip", ".rar", ".tar", ".7z" };

    /// <summary>注册文件关联，返回实际注册的扩展名列表。</summary>
    public static void Register(string exePath)
    {
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "ComicVerse 漫画/轻小说");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exePath}\",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        foreach (string ext in Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext);
            key.SetValue("", ProgId);
        }

        // 清理历史版本错误写入的通用扩展名（仅当它们确实指向本程序时才删除）
        foreach (string ext in LegacyExtensions)
            RemoveIfOwnedByUs(ext);

        NotifyShell();
    }

    /// <summary>
    /// 取消文件关联：删除 7 个扩展名指向本程序的 ProgId，并清理历史遗留的通用扩展名。
    /// 只删除值确实等于 <see cref="ProgId"/> 的项，不碰用户自己设置的其他关联。
    /// </summary>
    public static void Unregister()
    {
        foreach (string ext in Extensions)
            RemoveIfOwnedByUs(ext);

        foreach (string ext in LegacyExtensions)
            RemoveIfOwnedByUs(ext);

        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); }
        catch { /* 被占用/不存在则忽略 */ }

        NotifyShell();
    }

    /// <summary>注册表里当前指向本程序的扩展名，用于界面显示。</summary>
    public static bool IsRegistered()
    {
        try
        {
            foreach (string ext in Extensions)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext);
                if (key?.GetValue("") as string != ProgId)
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveIfOwnedByUs(string ext)
    {
        try
        {
            string path = @"Software\Classes\" + ext;
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null) return;
            if (key.GetValue("") as string != ProgId) return;
            key.DeleteValue("", throwOnMissingValue: false);
        }
        catch
        {
            // 单条失败不影响其余
        }
    }

    /// <summary>通知 shell 关联已变更，使资源管理器的图标/双击行为立即更新。</summary>
    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
