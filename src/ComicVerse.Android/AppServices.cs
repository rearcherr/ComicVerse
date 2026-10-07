using Android.Content;
using ComicVerse.Core;
using ComicVerse.Core.Services;

namespace ComicVerse.Droid;

/// <summary>全局服务容器：数据目录、书库、设置、导入器、共享图片缓存。</summary>
public static class AppServices
{
    public static string AppDataDir { get; private set; } = "";
    public static LibraryService Library { get; private set; } = null!;
    public static AppSettingsService Settings { get; private set; } = null!;
    public static ImportService Importer { get; private set; } = null!;
    public static ImageCacheService Cache { get; } = new();
    public static bool Initialized { get; private set; }

    public static void Init(Context context)
    {
        if (Initialized) return;
        Initialized = true;

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            Log.Error("SQLite 初始化失败", ex);
        }

        AppDataDir = context.FilesDir?.AbsolutePath ?? Path.GetTempPath();
        Directory.CreateDirectory(AppDataDir);
        Log.Configure(Path.Combine(AppDataDir, "logs"));

        Library = new LibraryService(Path.Combine(AppDataDir, "library.db"));
        Settings = new AppSettingsService(Library);
        Importer = new ImportService(Library);
        Cache.MaxBytes = Settings.CacheLimitMb * 1024 * 1024;
    }

    /// <summary>安卓是沙盒存储，导入时把文件复制到应用私有目录。</summary>
    public static string ImportDir
    {
        get
        {
            string dir = Path.Combine(AppDataDir, "imports");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
