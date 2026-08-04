using System.IO;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string? overrideDir = Environment.GetEnvironmentVariable("COMICVERSE_DATA_DIR");
        AppDataDir = string.IsNullOrEmpty(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicVerse")
            : Path.GetFullPath(overrideDir);
        Directory.CreateDirectory(AppDataDir);
        Log.Configure(Path.Combine(AppDataDir, "logs"));

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
        if (fileArg is not null)
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
            main.OpenBookByPath(fileArg);
        }
        else
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }

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
