using System.IO;
using System.Windows;
using ComicVerse.App.Helpers;

namespace ComicVerse.App.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadValues();
    }

    private void LoadValues()
    {
        if (App.Settings.Theme == "light") LightRadio.IsChecked = true;
        else DarkRadio.IsChecked = true;

        CacheSlider.Value = App.Settings.CacheLimitMb;
        CacheText.Text = $"当前 {App.Settings.CacheLimitMb} MB（解码页面的内存上限）";
        int prefetch = App.Settings.PrefetchPages;
        PrefetchBox.SelectedIndex = prefetch switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, _ => 7 };
        LowPerfToggle.IsChecked = App.Settings.LowPerformanceMode;
        RtlToggle.IsChecked = App.Settings.MangaRightToLeft;
        switch (App.Settings.DefaultComicMode)
        {
            case "paged": DefaultPagedRadio.IsChecked = true; break;
            case "double": DefaultDoubleRadio.IsChecked = true; break;
            default: DefaultWebtoonRadio.IsChecked = true; break;
        }
    }

    private void CacheSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        long mb = (long)Math.Round(e.NewValue / 64) * 64;
        App.Settings.CacheLimitMb = Math.Max(128, mb);
        App.SharedCache.MaxBytes = App.Settings.CacheLimitMb * 1024 * 1024;
        CacheText.Text = $"当前 {App.Settings.CacheLimitMb} MB（解码页面的内存上限）";
    }

    private void PrefetchBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Settings.PrefetchPages = PrefetchBox.SelectedIndex switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, _ => 8 };
    }

    private void LowPerf_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Settings.LowPerformanceMode = LowPerfToggle.IsChecked == true;
    }

    private void Rtl_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Settings.MangaRightToLeft = RtlToggle.IsChecked == true;
    }

    private void DefaultMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (DefaultPagedRadio.IsChecked == true) App.Settings.DefaultComicMode = "paged";
        else if (DefaultDoubleRadio.IsChecked == true) App.Settings.DefaultComicMode = "double";
        else App.Settings.DefaultComicMode = "webtoon";
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.AppDataDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{App.AppDataDir}\"") { UseShellExecute = true });
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Library.Backup();
            HintText.Text = "已备份进度数据库到数据目录。";
        }
        catch (Exception ex)
        {
            HintText.Text = "备份失败：" + ex.Message;
        }
    }

    private void RegisterAssoc_Click(object sender, RoutedEventArgs e)
    {
        string exts = string.Join(" / ", FileAssociations.Extensions);
        var confirm = MessageBox.Show(
            $"将把以下扩展名在本用户下的默认打开方式设为 ComicVerse：\n\n{exts}\n\n" +
            "此操作只修改当前用户（HKEY_CURRENT_USER），可随时用「取消文件关联」还原。\n是否继续？",
            "注册文件关联",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ComicVerse.exe");
            FileAssociations.Register(exe);
            HintText.Text = $"已注册 {exts} 文件关联，双击即可用 ComicVerse 打开。";
        }
        catch (Exception ex)
        {
            HintText.Text = "注册失败：" + ex.Message;
        }
    }

    private void UnregisterAssoc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Unregister();
            HintText.Text = "已取消文件关联，相关扩展名已恢复系统默认打开方式。";
        }
        catch (Exception ex)
        {
            HintText.Text = "取消关联失败：" + ex.Message;
        }
    }
}
