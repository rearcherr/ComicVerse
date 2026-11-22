using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ComicVerse.App.Updates;
using ComicVerse.Core;
using ComicVerse.Core.Updates;

namespace ComicVerse.App.Windows;

public partial class UpdateWindow : Window
{
    private const string OfficialReleasePage = "https://github.com/rearcherr/ComicVerse/releases";
    private readonly AppUpdateCoordinator _coordinator;
    private CancellationTokenSource? _operationCts;
    private UpdateRelease? _release;
    private int _operationVersion;
    private bool _started;
    private bool _busy;
    private bool _downloading;
    private bool _applying;
    private bool _closed;
    private bool _disposed;

    public UpdateWindow() : this(new AppUpdateCoordinator()) { }

    internal UpdateWindow(AppUpdateCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        InstalledVersionText.Text = "v" + App.AppVersion;
        InstalledVersionText.ToolTip = InstalledVersionText.Text;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await CheckAsync();
    }

    private bool IsCurrent(int version) => !_closed && version == _operationVersion;

    private async Task CheckAsync()
    {
        if (_closed || _busy) return;
        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        int version = ++_operationVersion;
        _busy = true;
        _release = null;
        LatestVersionText.Text = "—";
        LatestVersionText.ToolTip = null;
        PackageSizeText.Visibility = Visibility.Collapsed;
        SetBusy("正在检查更新…", "正在连接官方发布源。", downloading: false);
        try
        {
            var result = await _coordinator.CheckAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(version)) return;
            LatestVersionText.Text = result.Release is { } latest ? "v" + latest.Version : "v" + App.AppVersion;
            LatestVersionText.ToolTip = LatestVersionText.Text;
            if (result.IsUpdateAvailable && result.Release is { } release)
            {
                _release = release;
                SetIdle("发现新版本", "更新会保存阅读进度，然后重新启动 ComicVerse。", "更新并重启");
                if (release.Size > 0)
                {
                    PackageSizeText.Text = "下载大小：" + FormatBytes(release.Size);
                    PackageSizeText.Visibility = Visibility.Visible;
                }
            }
            else
            {
                SetIdle("当前已是最新版本", "可以继续阅读，也可以前往官方发布页查看版本说明。", "再次检查");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (IsCurrent(version)) SetIdle("已取消检查", "准备好后可以重新检查更新。", "重新检查");
        }
        catch (Exception ex)
        {
            Log.Error("检查应用更新失败", ex);
            if (IsCurrent(version))
                SetIdle("暂时无法检查更新", "请检查网络连接后重试，也可以前往官方发布页查看版本。", "重试检查");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private async Task DownloadAndRestartAsync(UpdateRelease release)
    {
        if (_closed || _busy) return;
        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        int version = ++_operationVersion;
        _busy = true;
        _downloading = true;
        SetBusy("正在下载更新…", "下载完成并校验后，会保存阅读进度并重新启动。", downloading: true);
        DownloadedUpdate? downloaded = null;
        bool handedOff = false;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (!IsCurrent(version) || !_downloading || _applying || cts.IsCancellationRequested) return;
                ShowProgress(value);
            });
            downloaded = await _coordinator.DownloadAsync(release, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(version)) return;

            _downloading = false;
            _applying = true;
            StatusText.Text = "正在准备更新…";
            DetailText.Text = "正在保存阅读进度，随后将重新启动 ComicVerse。";
            ProgressText.Text = "下载与校验已完成";
            DownloadProgressBar.IsIndeterminate = true;
            CancelButton.IsEnabled = false;
            ReleasePageButton.IsEnabled = false;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            if (_closed) return;

            // The coordinator owns the staged update once handed off. Closing
            // this dialog during Application.Shutdown must not discard it.
            await _coordinator.RestartToInstallAsync(downloaded);
            handedOff = true;
            downloaded = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (IsCurrent(version))
                SetIdle("已取消下载", "当前版本可继续使用。准备好后可重新下载。", "重新下载并更新");
        }
        catch (Exception ex)
        {
            Log.Error(_applying ? "交接应用更新失败" : "下载应用更新失败", ex);
            if (IsCurrent(version))
            {
                SetIdle(_applying ? "暂时无法完成更新" : "更新下载失败",
                    _applying ? "请稍后重试，也可以从官方发布页下载。" : "请检查网络连接与可用磁盘空间，然后重试。",
                    "重试更新");
            }
        }
        finally
        {
            if (!handedOff && downloaded is not null)
            {
                try { _coordinator.Discard(downloaded); }
                catch (Exception ex) { Log.Error("清理未交接的应用更新失败", ex); }
            }
            _applying = false;
            EndOperation(cts);
        }
    }

    private void SetBusy(string status, string detail, bool downloading)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        ActionButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = downloading ? "取消下载" : "取消检查";
        ReleasePageButton.IsEnabled = true;
        PackageSizeText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = downloading ? "正在连接下载…" : "";
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.IsIndeterminate = true;
    }

    private void SetIdle(string status, string detail, string action)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        ActionButton.Content = action;
        ActionButton.IsEnabled = true;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;
        ReleasePageButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        DownloadProgressBar.IsIndeterminate = false;
    }

    private void ShowProgress(UpdateDownloadProgress value)
    {
        long received = Math.Max(0, value.BytesReceived);
        long? total = value.TotalBytes;
        double? fraction = value.Fraction;
        bool known = total is > 0 && fraction is double f && double.IsFinite(f);
        DownloadProgressBar.IsIndeterminate = !known;
        if (known) DownloadProgressBar.Value = Math.Clamp(fraction!.Value * 100, 0, 100);
        ProgressText.Text = total is > 0
            ? $"{FormatBytes(received)} / {FormatBytes(total.Value)}"
            : "已下载 " + FormatBytes(received);
        if (total is > 0 && received >= total.Value)
        {
            StatusText.Text = "正在校验更新…";
            DetailText.Text = "校验完成后会保存阅读进度并重新启动。";
        }
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
        _busy = false;
        _downloading = false;
        if (_closed) DisposeCoordinator();
    }

    private static string FormatBytes(long bytes)
    {
        double size = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _applying) return;
        if (_release is { } release) await DownloadAndRestartAsync(release);
        else await CheckAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelOrClose();

    private void CancelOrClose()
    {
        if (_applying) return;
        if (!_busy) { Close(); return; }
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        _operationCts?.Cancel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CancelOrClose();
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        bool exitingForUpdate = Application.Current is App app && app.IsExitingForUpdate;
        if (_applying && !exitingForUpdate) { e.Cancel = true; return; }
        if (!_applying) _operationCts?.Cancel();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _operationVersion++;
        if (!_applying) _operationCts?.Cancel();
        if (!_busy) DisposeCoordinator();
    }

    private void DisposeCoordinator()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.Dispose();
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(OfficialReleasePage) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error("打开官方发布页失败", ex);
            if (!_busy) DetailText.Text = "暂时无法打开浏览器，请稍后重试。";
        }
    }
}
