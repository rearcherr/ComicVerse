using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace ComicVerse.Droid.Ui;

/// <summary>应用设置：主题、默认阅读方式、阅读方向、预取与缓存策略。</summary>
public class AppSettingsDialog
{
    private readonly Activity _activity;
    private readonly Action _onThemeChanged;

    public AppSettingsDialog(Activity activity, Action onThemeChanged)
    {
        _activity = activity;
        _onThemeChanged = onThemeChanged;
    }

    public void Show()
    {
        var content = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        content.SetPadding(UiKit.Dp(18), UiKit.Dp(10), UiKit.Dp(18), UiKit.Dp(14));

        AddLabel(content, "主题");
        AddChips(content, new[] { "深色", "浅色" }, () => AppTheme.Dark ? 0 : 1, index =>
        {
            AppTheme.Dark = index == 0;
            AppServices.Settings.Theme = AppTheme.Dark ? "dark" : "light";
            _onThemeChanged?.Invoke();
        });

        AddLabel(content, "漫画默认打开方式");
        AddChips(content, new[] { "翻页", "条漫", "双页" }, () => AppServices.Settings.DefaultComicMode switch
        {
            "paged" => 0,
            "double" => 2,
            _ => 1
        }, index =>
        {
            AppServices.Settings.DefaultComicMode = index switch { 0 => "paged", 2 => "double", _ => "webtoon" };
        });

        AddLabel(content, "日漫阅读方向");
        AddChips(content, new[] { "左 → 右", "右 → 左（RTL）" }, () => AppServices.Settings.MangaRightToLeft ? 1 : 0, index =>
        {
            AppServices.Settings.MangaRightToLeft = index == 1;
        });

        AddLabel(content, "低性能模式（预取减半）");
        AddChips(content, new[] { "关闭", "开启" }, () => AppServices.Settings.LowPerformanceMode ? 1 : 0, index =>
        {
            AppServices.Settings.LowPerformanceMode = index == 1;
        });

        AddSlider(content, "后台预取页数", 0, 8, 1, AppServices.Settings.PrefetchPages, false, value =>
        {
            AppServices.Settings.PrefetchPages = value;
        });

        AddSlider(content, "图片缓存上限", 128, 2048, 64, (int)Math.Clamp(AppServices.Settings.CacheLimitMb, 128, 2048), false, value =>
        {
            AppServices.Settings.CacheLimitMb = value;
            AppServices.Cache.MaxBytes = value * 1024L * 1024L;
            UiKit.Toast(_activity, $"缓存上限已设为 {value} MB");
        });

        AddLabel(content, "维护");
        var clear = UiKit.Chip(_activity, "清空图片缓存", false, 12.5);
        clear.Click += (_, _) =>
        {
            AppServices.Cache.Clear();
            UiKit.Toast(_activity, "已清空图片缓存");
        };
        content.AddView(clear);

        var info = UiKit.Text(_activity, "书库与封面存放在应用私有目录：\n" + AppServices.AppDataDir, 11, AppTheme.TextFaint);
        var infoParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        infoParams.TopMargin = UiKit.Dp(14);
        content.AddView(info, infoParams);

        var scroll = new ScrollView(_activity);
        scroll.AddView(content);

        new AlertDialog.Builder(_activity)!
            .SetTitle("设置")!
            .SetView(scroll)!
            .SetNegativeButton("关闭", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    private void AddLabel(LinearLayout parent, string text)
    {
        var label = UiKit.Text(_activity, text, 12.5, AppTheme.TextSecondary, true);
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        parameters.TopMargin = UiKit.Dp(14);
        parameters.BottomMargin = UiKit.Dp(6);
        parent.AddView(label, parameters);
    }

    private void AddChips(LinearLayout parent, string[] labels, Func<int> current, Action<int> onSelected)
    {
        int selected = current();
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var chip = UiKit.Chip(_activity, labels[i], i == selected, 12);
            chip.Click += (_, _) => onSelected(index);
            var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            parameters.RightMargin = UiKit.Dp(6);
            row.AddView(chip, parameters);
        }
        parent.AddView(row);
    }

    private void AddSlider(LinearLayout parent, string label, int min, int max, int step, int value,
        bool decimalDisplay, Action<int> onChanged)
    {
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        var title = UiKit.Text(_activity, label, 12.5, AppTheme.TextSecondary, true);
        var valueText = UiKit.Text(_activity, decimalDisplay ? (value / 100.0).ToString("0.0") : value.ToString(), 12, AppTheme.TextPrimary);
        valueText.Gravity = GravityFlags.Right;
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f });
        header.AddView(valueText);
        var headerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        headerParams.TopMargin = UiKit.Dp(12);
        parent.AddView(header, headerParams);

        var seek = new SeekBar(_activity) { Max = Math.Max(1, (max - min) / step) };
        seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        seek.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.ProgressTrack);
        seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        seek.Progress = Math.Clamp((value - min) / step, 0, seek.Max);
        seek.ProgressChanged += (_, e) =>
        {
            if (!e.FromUser) return;
            int mapped = min + e.Progress * step;
            valueText.Text = decimalDisplay ? (mapped / 100.0).ToString("0.0") : mapped.ToString();
        };
        seek.StopTrackingTouch += (_, e) => onChanged(min + e.SeekBar!.Progress * step);
        parent.AddView(seek, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }
}
