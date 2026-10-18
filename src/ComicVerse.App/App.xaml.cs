using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Services;
using ComicVerse.App.Windows;

namespace ComicVerse.App;

public partial class App : Application
{
    public static LibraryService Library = null!;
    public static AppSettingsService Settings = null!;
    public static ImportService Importer = null!;
    public static ImageCacheService SharedCache = new();

    public static string AppDataDir { get; private set; } = "";

    /// <summary>应用版本号（来自程序集信息，CI 发布时与 git 标签一致），用于界面显示与日志排查。</summary>
    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底必须最先安装：项目里有 5 个 async void 入口，
        // 没有钩子时任何 UI 线程异常都会让进程静默消失、连日志都不留。
        InstallGlobalExceptionHandlers();

        try
        {
            await StartupCoreAsync(e);
        }
        catch (Exception ex)
        {
            // OnStartup 是 async void，异常不会被任何调用方捕获，必须在这里自己收口。
            Log.Error("启动失败", ex);
            try
            {
                MessageBox.Show($"ComicVerse 启动失败：\n{ex.Message}\n\n日志目录：\n{Path.Combine(AppDataDir, "logs")}",
                    "ComicVerse", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
            }
            Shutdown(1);
        }
    }

    private static void InstallGlobalExceptionHandlers()
    {
        // UI 线程异常（DispatcherTimer.Tick / BeginInvoke 等）会走到这里；不处理则进程直接终止。
        if (Current is not null)
        {
            Current.DispatcherUnhandledException += (_, ex) =>
            {
                Log.Error("UI 线程未处理异常", ex.Exception);
                try
                {
                    string logDir = Path.Combine(AppDataDir, "logs");
                    MessageBox.Show($"发生错误，已写入日志：\n{logDir}\n\n{ex.Exception.Message}",
                        "ComicVerse", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                }
                ex.Handled = true; // 尽量让主界面活下来，而不是整个进程消失
            };
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error("进程级未处理异常", ex.ExceptionObject as Exception);

        // 未观察的 Task 异常（fire-and-forget）不会进 Dispatcher，只在这里出现。
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("未观察的 Task 异常", ex.Exception);
            ex.SetObserved();
        };
    }

    private async Task StartupCoreAsync(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string? overrideDir = Environment.GetEnvironmentVariable("COMICVERSE_DATA_DIR");
        AppDataDir = string.IsNullOrEmpty(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicVerse")
            : Path.GetFullPath(overrideDir);
        Directory.CreateDirectory(AppDataDir);
        Log.Configure(Path.Combine(AppDataDir, "logs"));
        Log.Info($"ComicVerse v{AppVersion} 启动 | exe={Environment.ProcessPath} | 数据目录={AppDataDir}");
        try
        {
            PdfNative.EnsureExtracted(Path.Combine(AppDataDir, "native"));
        }
        catch (Exception ex)
        {
            Log.Error("提取 pdfium 失败", ex);
        }

        Library = new LibraryService(Path.Combine(AppDataDir, "library.db"));
        Settings = new AppSettingsService(Library);
        Importer = new ImportService(Library);
        SharedCache.MaxBytes = Settings.CacheLimitMb * 1024 * 1024;

        ThemeService.Apply(Settings.Theme);

        if (e.Args.Contains("--smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await SmokeTest.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        var splash = new SplashWindow();
        splash.Show();

        string? fileArg = e.Args.FirstOrDefault(a => File.Exists(a));
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        if (fileArg is not null)
            main.OpenBookByPath(fileArg);

        splash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Library?.Dispose();
        }
        catch
        {
        }
        base.OnExit(e);
    }
}
