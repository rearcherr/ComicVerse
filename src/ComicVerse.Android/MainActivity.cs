using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;
using ComicVerse.Droid.Services;
using ComicVerse.Droid.Ui;
using Path = System.IO.Path;

namespace ComicVerse.Droid;

[Activity(Label = "ComicVerse",
    MainLauncher = true,
    Icon = "@drawable/appicon",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeTypes = new[]
    {
        "application/pdf", "application/epub+zip", "application/zip", "application/x-cbz",
        "application/vnd.comicbook+zip", "application/x-rar-compressed", "application/vnd.rar",
        "application/x-7z-compressed", "application/x-tar", "text/plain"
    })]
[IntentFilter(new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "*/*" })]
public class MainActivity : Activity
{
    private const int RequestPickFiles = 1001;
    private const int RequestPickFolder = 1002;

    private FrameLayout _root = null!;
    private LibraryView? _library;
    private ReaderView? _reader;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppServices.Init(this);
        AppTheme.Dark = AppServices.Settings.Theme != "light";

        _root = new FrameLayout(this);
        SetContentView(_root);
        ApplySystemBars();
        ShowLibrary();
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void ApplySystemBars()
    {
        var window = Window;
        if (window is null) return;
        window.SetStatusBarColor(AppTheme.WindowBg);
        window.SetNavigationBarColor(AppTheme.WindowBg);
        if (window.InsetsController is { } controller)
        {
            int light = (int)WindowInsetsControllerAppearance.LightStatusBars;
            controller.SetSystemBarsAppearance(AppTheme.Dark ? 0 : light, light);
        }
    }

    // ------------------------------------------------------------ 书架

    private void ShowLibrary()
    {
        _reader?.Close();
        if (_reader is not null)
        {
            _root.RemoveView(_reader);
            _reader = null;
        }

        if (_library is null)
        {
            _library = new LibraryView(this)
            {
                BookOpened = book => OpenReader(book, false),
                BookOpenedFromStart = book => OpenReader(book, true),
                AddFilesRequested = PickFiles,
                AddFolderRequested = PickFolder,
                SettingsRequested = ShowSettings,
                ThemeToggleRequested = ToggleTheme
            };
            _root.AddView(_library, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }
        _library.Visibility = ViewStates.Visible;
        _library.Refresh();
    }

    private void ToggleTheme()
    {
        AppTheme.Dark = !AppTheme.Dark;
        AppServices.Settings.Theme = AppTheme.Dark ? "dark" : "light";
        ApplySystemBars();
        _library?.ClearCoverCache();
        _root.RemoveView(_library);
        _library = null;
        ShowLibrary();
    }

    private void ShowSettings()
    {
        new AppSettingsDialog(this, () =>
        {
            ApplySystemBars();
            _library?.ClearCoverCache();
            _root.RemoveView(_library);
            _library = null;
            ShowLibrary();
        }).Show();
    }

    // ------------------------------------------------------------ 阅读器

    private void OpenReader(Book book, bool fromStart)
    {
        _reader?.Close();
        if (_reader is not null) _root.RemoveView(_reader);

        var reader = new ReaderView(this, book, fromStart);
        reader.CloseRequested += () => ShowLibrary();
        reader.ThemeToggleRequested += () =>
        {
            AppTheme.Dark = !AppTheme.Dark;
            AppServices.Settings.Theme = AppTheme.Dark ? "dark" : "light";
            ApplySystemBars();
            reader.RefreshTheme();
        };
        _reader = reader;
        _root.AddView(reader, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _library!.Visibility = ViewStates.Gone;
        reader.Start();
    }

    public override void OnBackPressed()
    {
        if (_reader is not null)
        {
            if (_reader.HandleBack()) return;
            ShowLibrary();
            return;
        }
        base.OnBackPressed();
    }

    // ------------------------------------------------------------ 导入

    private void PickFiles()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, RequestPickFiles);
    }

    private void PickFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        StartActivityForResult(intent, RequestPickFolder);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;

        if (requestCode == RequestPickFiles)
        {
            if (data.ClipData is not null)
            {
                var uris = new List<Android.Net.Uri>();
                for (int i = 0; i < data.ClipData.ItemCount; i++)
                {
                    var uri = data.ClipData.GetItemAt(i)?.Uri;
                    if (uri is not null) uris.Add(uri);
                }
                _ = ImportUrisAsync(uris);
            }
            else
            {
                _ = ImportUrisAsync(new[] { data.Data });
            }
        }
        else if (requestCode == RequestPickFolder)
        {
            _ = ImportTreeAsync(data.Data);
        }
    }

    private async Task ImportUrisAsync(IEnumerable<Android.Net.Uri> uris)
    {
        _library?.SetStatus("正在复制文件…");
        var paths = new List<string>();
        await Task.Run(() =>
        {
            foreach (var uri in uris)
            {
                try
                {
                    paths.Add(StorageImporter.CopyFile(this, uri));
                }
                catch (Exception ex)
                {
                    Log.Error("复制导入文件失败", ex);
                }
            }
        });
        await ImportPathsAsync(paths);
    }

    private async Task ImportTreeAsync(Android.Net.Uri treeUri)
    {
        _library?.SetStatus("正在扫描文件夹…");
        var progress = new Progress<string>(name => _library?.SetStatus("正在复制：" + name));
        string? root = null;
        await Task.Run(() =>
        {
            root = StorageImporter.CopyTree(this, treeUri, progress);
        });
        if (string.IsNullOrEmpty(root))
        {
            _library?.SetStatus("没有找到可导入的文件夹");
            return;
        }
        await ImportPathsAsync(new[] { root! });
    }

    private async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            _library?.SetStatus("没有找到可导入的文件");
            return;
        }

        var progress = new Progress<ImportProgress>(p =>
            _library?.SetStatus($"正在导入 ({p.Done}/{p.Total})：{Path.GetFileName(p.Current.TrimEnd('/', '\\'))}"));

        ImportResult result;
        try
        {
            result = await AppServices.Importer.ImportAsync(paths, progress);
        }
        catch (Exception ex)
        {
            Log.Error("导入失败", ex);
            _library?.SetStatus("导入失败：" + ex.Message);
            return;
        }

        _library?.Refresh();
        string summary = $"导入完成：新增 {result.Imported} 本，更新 {result.Updated} 本";
        if (result.Failed.Count > 0)
        {
            summary += $"，失败 {result.Failed.Count} 个";
            ShowMessage("导入提示", string.Join("\n", result.Failed.Take(10)));
        }
        _library?.SetStatus(summary);
    }

    private void ShowMessage(string title, string message)
    {
        new AlertDialog.Builder(this)!
            .SetTitle(title)!
            .SetMessage(message)!
            .SetPositiveButton("知道了", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    // ------------------------------------------------------------ 外部打开

    private void HandleIntent(Intent? intent)
    {
        if (intent is null) return;
        if (intent.Action != Intent.ActionView && intent.Action != Intent.ActionSend) return;

        var uris = new List<Android.Net.Uri>();
        if (intent.Data is not null) uris.Add(intent.Data);
        if (intent.ClipData is not null)
        {
            for (int i = 0; i < intent.ClipData.ItemCount; i++)
            {
                var uri = intent.ClipData.GetItemAt(i)?.Uri;
                if (uri is not null) uris.Add(uri);
            }
        }
        if (uris.Count == 0) return;
        _ = ImportAndOpenAsync(uris);
    }

    private async Task ImportAndOpenAsync(IReadOnlyList<Android.Net.Uri> uris)
    {
        _library?.SetStatus("正在打开文件…");
        string? opened = null;
        var paths = new List<string>();
        await Task.Run(() =>
        {
            foreach (var uri in uris)
            {
                try
                {
                    paths.Add(StorageImporter.CopyFile(this, uri));
                }
                catch (Exception ex)
                {
                    Log.Error("打开外部文件失败", ex);
                }
            }
        });

        if (paths.Count == 0)
        {
            _library?.SetStatus("无法读取该文件");
            return;
        }

        var result = await AppServices.Importer.ImportAsync(paths);
        _library?.Refresh();
        if (result.Failed.Count > 0)
        {
            ShowMessage("打开失败", string.Join("\n", result.Failed.Take(6)));
            return;
        }

        opened = paths[0];
        var book = AppServices.Library.GetBookByPath(Path.GetFullPath(opened));
        if (book is not null) OpenReader(book, fromStart: false);
    }
}
