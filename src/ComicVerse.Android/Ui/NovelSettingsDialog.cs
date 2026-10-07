using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using ComicVerse.Core.Models;
using ComicVerse.Droid.Services;

namespace ComicVerse.Droid.Ui;

/// <summary>小说排版设置：字体、字号、行距、段间距、页边距、文字/背景色与 TXT 编码。</summary>
public class NovelSettingsDialog
{
    private readonly Activity _activity;
    private readonly NovelViewSettings _settings;
    private readonly bool _allowEncoding;
    private readonly Action<NovelViewSettings, bool> _onChanged;
    private readonly Action<EncodingOverride> _onEncodingChanged;

    private EncodingOverride _encoding = EncodingOverride.Auto;

    public NovelSettingsDialog(Activity activity, NovelViewSettings settings, int autoSpeed, bool allowEncoding,
        Action<NovelViewSettings, bool> onChanged, Action<EncodingOverride> onEncodingChanged)
    {
        _activity = activity;
        _settings = settings;
        _allowEncoding = allowEncoding;
        _onChanged = onChanged;
        _onEncodingChanged = onEncodingChanged;
    }

    public void Show()
    {
        var content = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        content.SetPadding(UiKit.Dp(18), UiKit.Dp(12), UiKit.Dp(18), UiKit.Dp(12));

        AddLabel(content, "字体");
        AddChips(content, new[] { "系统默认", "衬线", "等宽" }, () =>
            _settings.FontFamily.Contains("serif") ? 1 : _settings.FontFamily.Contains("mono") ? 2 : 0,
            index =>
            {
                _settings.FontFamily = index switch { 1 => "serif", 2 => "monospace", _ => "sans-serif" };
                Commit();
            });

        AddSlider(content, "字号", 12, 32, (int)Math.Round(_settings.FontSize), 1, false, value =>
        {
            _settings.FontSize = value;
            Commit();
        });

        AddSlider(content, "行距", 120, 250, (int)Math.Round(_settings.LineSpacing * 100), 10, true, value =>
        {
            _settings.LineSpacing = value / 100.0;
            Commit();
        });

        AddSlider(content, "段间距", 0, 24, (int)Math.Round(_settings.ParagraphSpacing), 2, false, value =>
        {
            _settings.ParagraphSpacing = value;
            Commit();
        });

        AddSlider(content, "页边距", 24, 120, (int)Math.Round(_settings.PageMargin), 4, false, value =>
        {
            _settings.PageMargin = value;
            Commit();
        });

        AddLabel(content, "文字颜色");
        var textColors = new[] { "#E8E8F4", "#FFFFFF", "#F5E6C8", "#2F2A3E", "#1F1F28", "#C9F2E0" };
        var textNames = new[] { "默认", "纯白", "米黄", "深墨字", "墨黑", "青绿" };
        AddChips(content, textNames, () => Math.Max(0, Array.IndexOf(textColors, _settings.TextColor)), index =>
        {
            _settings.TextColor = textColors[index];
            Commit();
        });

        AddLabel(content, "背景色");
        var backgrounds = new[] { "#1A1A2E", "#101014", "#FBF4E8", "#FAF6F8", "#FFF5F7" };
        var backgroundNames = new[] { "深蓝紫", "纯黑", "米白", "阅读白", "淡粉白" };
        AddChips(content, backgroundNames, () => Math.Max(0, Array.IndexOf(backgrounds, _settings.Background)), index =>
        {
            _settings.Background = backgrounds[index];
            Commit();
        });

        if (_allowEncoding)
        {
            AddLabel(content, "文本编码（TXT）");
            AddChips(content, new[] { "自动检测", "UTF-8", "GBK", "Big5", "Shift-JIS" }, () => (int)_encoding, index =>
            {
                _encoding = (EncodingOverride)index;
                _onEncodingChanged?.Invoke(_encoding);
            });
        }

        var scroll = new ScrollView(_activity);
        scroll.AddView(content);

        new AlertDialog.Builder(_activity)!
            .SetTitle("排版设置")!
            .SetView(scroll)!
            .SetNegativeButton("关闭", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    private void Commit()
    {
        AppServices.Settings.NovelSettings = _settings;
        _onChanged?.Invoke(_settings, true);
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
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.Left);
        int selected = current();
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var chip = UiKit.Chip(_activity, labels[i], i == selected, 12);
            chip.Click += (_, _) => onSelected(index);
            var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            parameters.RightMargin = UiKit.Dp(6);
            row.AddView(chip, parameters);
        }
        var scroll = new HorizontalScrollView(_activity);
        scroll.AddView(row);
        parent.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    private void AddSlider(LinearLayout parent, string label, int min, int max, int value, int step,
        bool decimalDisplay, Action<int> onChanged)
    {
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var title = UiKit.Text(_activity, label, 12.5, AppTheme.TextSecondary, true);
        var valueText = UiKit.Text(_activity, Display(value), 12, AppTheme.TextPrimary);
        valueText.Gravity = GravityFlags.Right;
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f });
        header.AddView(valueText);
        var headerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        headerParams.TopMargin = UiKit.Dp(12);
        parent.AddView(header, headerParams);

        var seek = new SeekBar(_activity)
        {
            Max = Math.Max(1, (max - min) / step)
        };
        seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        seek.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.ProgressTrack);
        seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        seek.Progress = Math.Clamp((value - min) / step, 0, seek.Max);
        seek.ProgressChanged += (_, e) =>
        {
            if (!e.FromUser) return;
            int mapped = min + e.Progress * step;
            valueText.Text = Display(mapped);
        };
        seek.StopTrackingTouch += (_, e) => onChanged(min + e.SeekBar!.Progress * step);
        parent.AddView(seek, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        string Display(int raw) => decimalDisplay ? (raw / 100.0).ToString("0.0") : raw.ToString();
    }
}
