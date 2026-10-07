using System.IO.Compression;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using ComicVerse.Core;
using ComicVerse.Core.Comics;
using ComicVerse.Core.Models;
using ComicVerse.Core.Novel;
using ComicVerse.Core.Services;
using ComicVerse.Droid.Services;
using OperationCanceledException = System.OperationCanceledException;

namespace ComicVerse.Droid.Ui;

/// <summary>
/// 阅读器界面：漫画三种模式（翻页/条漫/双页）+ 小说（翻页/滚动），
/// 缩放、进度跳转、书签、章节目录、排版设置与进度自动保存。
/// </summary>
public class ReaderView : FrameLayout
{
    private readonly Activity _activity;
    private readonly Book _book;
    private readonly bool _fromStart;

    private IComicSource? _source;
    private ComicImageLoader? _loader;
    private NovelBook? _novel;
    private ZipArchive? _epubZip;
    private Func<string, Stream>? _imageResolver;

    private readonly FrameLayout _contentHost;
    private readonly ComicPagerView _pager;
    private readonly WebtoonReaderView _webtoon;
    private readonly NovelReaderView _novelView;
    private readonly TextView _loading;
    private readonly LinearLayout _topBar;
    private readonly LinearLayout _bottomBar;
    private readonly LinearLayout _modeRow;
    private readonly LinearLayout _zoomRow;
    private readonly TextView _titleText;
    private readonly TextView _pageInfo;
    private TextView _zoomText = null!;
    private readonly SeekBar _progressBar;
    private SeekBar _zoomBar = null!;
    private readonly TextView _themeChip;
    private TextView _rtlChip = null!;

    private string _mode = "paged";
    private string _fitMode = "width";
    private double _zoomRelative = 1.0;
    private float _webtoonScale = 1.0f;
    private int _page;
    private int _spreadStart;
    private bool _doubleWide;
    private bool _rtl;
    private bool _webtoonReady;
    private bool _novelReady;
    private double _webtoonFraction;

    private int _novelChapter;
    private int _novelPage;
    private double _novelScrollFraction;
    private NovelViewSettings _novelSettings = new();
    private EncodingOverride _encoding = EncodingOverride.Auto;

    private CancellationTokenSource? _pageCts;
    private long _pageVersion;
    private bool _closed;
    private bool _barsVisible = true;
    private bool _updatingSlider;
    private bool _sliderActive;
    private readonly Handler _ui = new(Android.OS.Looper.MainLooper!);
    private readonly Java.Lang.Runnable _saveRunnable;

    public event Action? CloseRequested;
    public event Action? ThemeToggleRequested;

    public ReaderView(Activity activity, Book book, bool fromStart) : base(activity)
    {
        _activity = activity;
        _book = book;
        _fromStart = fromStart;
        _rtl = AppServices.Settings.MangaRightToLeft;
        _saveRunnable = new Java.Lang.Runnable(SaveNow);

        SetBackgroundColor(AppTheme.ReaderBg);

        _contentHost = new FrameLayout(activity);
        AddView(_contentHost, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _pager = new ComicPagerView(activity);
        _contentHost.AddView(_pager, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _pager.LeftZoneTapped += () => { if (_rtl) Next(); else Prev(); };
        _pager.RightZoneTapped += () => { if (_rtl) Prev(); else Next(); };
        _pager.CenterTapped += ToggleBars;
        _pager.ZoomChanged += z => Ui(() =>
        {
            _zoomRelative = z;
            _zoomText.Text = $"{(int)Math.Round(z * 100)}%";
            _updatingSlider = true;
            _zoomBar.Progress = (int)Math.Clamp(z * 100 - 20, 0, 580);
            _updatingSlider = false;
        });

        _webtoon = new WebtoonReaderView(activity) { Visibility = ViewStates.Gone };
        _contentHost.AddView(_webtoon, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _webtoon.CurrentPageChanged += idx => Ui(() =>
        {
            if (_mode != "webtoon" || _loader is null) return;
            _page = idx;
            _pageInfo.Text = $"{idx + 1} / {_loader.PageCount}";
            SetSlider((double)(idx + 1 + _webtoon.ScrollFraction) / _loader.PageCount);
            ScheduleSave();
        });
        _webtoon.ScaleChanged += scale => Ui(() =>
        {
            _webtoonScale = (float)scale;
            _zoomText.Text = $"{(int)Math.Round(scale * 100)}%";
            _updatingSlider = true;
            _zoomBar.Progress = (int)Math.Clamp(scale * 100 - 20, 0, 280);
            _updatingSlider = false;
        });
        _webtoon.Tapped += ToggleBars;

        _novelView = new NovelReaderView(activity) { Visibility = ViewStates.Gone };
        _contentHost.AddView(_novelView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _novelView.LeftZoneTapped += Prev;
        _novelView.RightZoneTapped += Next;
        _novelView.CenterTapped += ToggleBars;
        _novelView.PageChanged += (page, pages) => Ui(() =>
        {
            _novelPage = page;
            if (_novel is null) return;
            double fraction = _novelView.SubMode == "scroll"
                ? _novelView.ScrollFraction
                : pages <= 1 ? 0 : (double)page / (pages - 1);
            if (_novelView.SubMode == "scroll") _novelScrollFraction = fraction;
            _pageInfo.Text = $"第 {_novelChapter + 1}/{_novel.Chapters.Count} 章 · 第 {_novelView.PageIndex + 1}/{_novelView.PageCount} 页";
            SetSlider((_novelChapter + fraction) / Math.Max(1, _novel.Chapters.Count));
            ScheduleSave();
        });
        _novelView.NextChapterRequested += () => Ui(() =>
        {
            if (_novel is not null && _novelChapter < _novel.Chapters.Count - 1)
                LoadNovelChapter(_novelChapter + 1);
        });
        _novelView.PrevChapterRequested += () => Ui(() =>
        {
            if (_novelChapter > 0) LoadNovelChapter(_novelChapter - 1);
        });

        _loading = UiKit.Text(activity, "正在打开…", 13, AppTheme.TextSecondary);
        _loading.Gravity = GravityFlags.Center;
        var loadingParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center
        };
        _contentHost.AddView(_loading, loadingParams);

        // 顶栏
        _topBar = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _topBar.SetBackgroundColor(AppTheme.WindowBg);
        var topRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        topRow.SetPadding(UiKit.Dp(10), UiKit.Dp(8), UiKit.Dp(10), UiKit.Dp(6));
        topRow.SetGravity(GravityFlags.CenterVertical);

        var back = UiKit.IconButton(activity, "←");
        back.Click += (_, _) => CloseRequested?.Invoke();
        topRow.AddView(back);

        _titleText = UiKit.Text(activity, book.Title, 14.5, AppTheme.TextPrimary, true);
        _titleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _titleText.SetSingleLine(true);
        var titleParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f };
        titleParams.LeftMargin = UiKit.Dp(10);
        topRow.AddView(_titleText, titleParams);

        var bookmarkButton = UiKit.IconButton(activity, "🔖");
        bookmarkButton.Click += (_, _) => ShowBookmarks();
        topRow.AddView(bookmarkButton);

        _themeChip = UiKit.IconButton(activity, AppTheme.Dark ? "☀" : "☾");
        _themeChip.Click += (_, _) => ThemeToggleRequested?.Invoke();
        var themeParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        themeParams.LeftMargin = UiKit.Dp(6);
        topRow.AddView(_themeChip, themeParams);
        _topBar.AddView(topRow);

        _modeRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        _modeRow.SetPadding(UiKit.Dp(10), 0, UiKit.Dp(10), UiKit.Dp(8));
        _modeRow.SetGravity(GravityFlags.CenterVertical);
        _topBar.AddView(_modeRow);

        var topParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Top
        };
        AddView(_topBar, topParams);

        // 底栏
        _bottomBar = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _bottomBar.SetBackgroundColor(AppTheme.WindowBg);

        var navRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        navRow.SetPadding(UiKit.Dp(10), UiKit.Dp(6), UiKit.Dp(10), UiKit.Dp(4));
        navRow.SetGravity(GravityFlags.CenterVertical);

        var prev = UiKit.IconButton(activity, "‹", 18);
        prev.Click += (_, _) => Prev();
        navRow.AddView(prev);

        _pageInfo = UiKit.Text(activity, "1 / 1", 12, AppTheme.TextSecondary);
        var infoParams = new LinearLayout.LayoutParams(UiKit.Dp(96), ViewGroup.LayoutParams.WrapContent);
        infoParams.LeftMargin = UiKit.Dp(8);
        infoParams.RightMargin = UiKit.Dp(4);
        navRow.AddView(_pageInfo, infoParams);

        _progressBar = new SeekBar(activity) { Max = 1000 };
        _progressBar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        _progressBar.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.ProgressTrack);
        _progressBar.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        _progressBar.StartTrackingTouch += (_, _) => _sliderActive = true;
        _progressBar.StopTrackingTouch += (_, _) => { _sliderActive = false; ApplySliderJump(); };
        _progressBar.ProgressChanged += (_, e) =>
        {
            if (_updatingSlider || !e.FromUser) return;
            if (_sliderActive) return;
            ApplySliderJump();
        };
        navRow.AddView(_progressBar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f });

        var next = UiKit.IconButton(activity, "›", 18);
        next.Click += (_, _) => Next();
        navRow.AddView(next);
        _bottomBar.AddView(navRow);

        _zoomRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        _zoomRow.SetPadding(UiKit.Dp(10), 0, UiKit.Dp(10), UiKit.Dp(8));
        _zoomRow.SetGravity(GravityFlags.CenterVertical);
        _bottomBar.AddView(_zoomRow);

        BuildZoomRow();

        var bottomParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom
        };
        AddView(_bottomBar, bottomParams);
    }

    private void BuildZoomRow()
    {
        _zoomRow.RemoveAllViews();
        if (_mode == "novel")
        {
            _zoomRow.AddView(ModeChip("翻页", _novelView.SubMode == "paged", () => SetNovelSubMode("paged")));
            var scrollChip = ModeChip("滚动", _novelView.SubMode == "scroll", () => SetNovelSubMode("scroll"));
            var scrollParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            scrollParams.LeftMargin = UiKit.Dp(6);
            _zoomRow.AddView(scrollChip, scrollParams);

            var autoChip = ModeChip(_novelView.AutoScrolling ? "⏸ 自动滚动" : "▶ 自动滚动", _novelView.AutoScrolling, () =>
            {
                if (_novelView.AutoScrolling) _novelView.StopAutoScroll();
                else
                {
                    if (_novelView.SubMode != "scroll") SetNovelSubMode("scroll");
                    _novelView.StartAutoScroll(AppServices.Settings.AutoScrollSpeed);
                }
                BuildZoomRow();
            });
            var autoParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            autoParams.LeftMargin = UiKit.Dp(6);
            _zoomRow.AddView(autoChip, autoParams);

            _zoomRow.AddView(new Space(Context!), new LinearLayout.LayoutParams(0, 1) { Weight = 1f });
            var speed = UiKit.Text(Context!, $"速度 {AppServices.Settings.AutoScrollSpeed}", 11.5, AppTheme.TextSecondary);
            _zoomRow.AddView(speed);
            var speedBar = new SeekBar(Context!) { Max = 140, Progress = AppServices.Settings.AutoScrollSpeed - 20 };
            speedBar.ProgressChanged += (_, e) =>
            {
                if (!e.FromUser) return;
                int value = e.Progress + 20;
                AppServices.Settings.AutoScrollSpeed = value;
                speed.Text = $"速度 {value}";
                if (_novelView.AutoScrolling) _novelView.StartAutoScroll(value);
            };
            _zoomRow.AddView(speedBar, new LinearLayout.LayoutParams(UiKit.Dp(110), ViewGroup.LayoutParams.WrapContent));
            return;
        }

        _zoomRow.AddView(ModeChip("宽", _fitMode == "width", () => SetFit("width")));
        var pageChip = ModeChip("整页", _fitMode == "page", () => SetFit("page"));
        var pageParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        pageParams.LeftMargin = UiKit.Dp(6);
        _zoomRow.AddView(pageChip, pageParams);
        var actualChip = ModeChip("1:1", _fitMode == "actual", () => SetFit("actual"));
        var actualParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        actualParams.LeftMargin = UiKit.Dp(6);
        _zoomRow.AddView(actualChip, actualParams);

        var zoomOut = UiKit.IconButton(Context!, "−");
        zoomOut.Click += (_, _) => StepZoom(1 / 1.25);
        var zoomOutParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        zoomOutParams.LeftMargin = UiKit.Dp(8);
        _zoomRow.AddView(zoomOut, zoomOutParams);

        _zoomText = UiKit.Text(Context!, "100%", 11.5, AppTheme.TextSecondary);
        _zoomText.Gravity = GravityFlags.Center;
        _zoomRow.AddView(_zoomText, new LinearLayout.LayoutParams(UiKit.Dp(48), ViewGroup.LayoutParams.WrapContent));

        var zoomIn = UiKit.IconButton(Context!, "＋");
        zoomIn.Click += (_, _) => StepZoom(1.25);
        _zoomRow.AddView(zoomIn);

        int max = _mode == "webtoon" ? 280 : 580;
        double current = _mode == "webtoon" ? _webtoonScale * 100 : _zoomRelative * 100;
        _zoomBar = new SeekBar(Context!)
        {
            Max = max,
            Progress = (int)Math.Clamp(current - 20, 0, max)
        };
        _zoomBar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        _zoomBar.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.ProgressTrack);
        _zoomBar.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(AppTheme.Accent);
        _zoomBar.ProgressChanged += (_, e) =>
        {
            if (!e.FromUser || _updatingSlider) return;
            double value = (e.Progress + 20) / 100.0;
            if (_mode == "webtoon") _webtoon.SetScaleFactor(value);
            else _pager.SetZoomRelative(value);
        };
        _zoomRow.AddView(_zoomBar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f });
    }

    private TextView ModeChip(string text, bool active, Action action)
    {
        var chip = UiKit.Chip(Context!, text, active, 12);
        chip.Click += (_, _) => action();
        return chip;
    }

    private void BuildModeRow()
    {
        _modeRow.RemoveAllViews();
        if (_book.Type == BookType.Novel)
        {
            _modeRow.AddView(ModeChip("目录", false, ShowChapters));
            var settingsChip = ModeChip("排版", false, ShowNovelSettings);
            var settingsParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            settingsParams.LeftMargin = UiKit.Dp(6);
            _modeRow.AddView(settingsChip, settingsParams);
            _modeRow.AddView(new Space(Context!), new LinearLayout.LayoutParams(0, 1) { Weight = 1f });
            return;
        }

        _modeRow.AddView(ModeChip("翻页", _mode == "paged", () => SwitchMode("paged")));
        var webtoonChip = ModeChip("条漫", _mode == "webtoon", () => SwitchMode("webtoon"));
        var webtoonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        webtoonParams.LeftMargin = UiKit.Dp(6);
        _modeRow.AddView(webtoonChip, webtoonParams);
        var doubleChip = ModeChip("双页", _mode == "double", () => SwitchMode("double"));
        var doubleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        doubleParams.LeftMargin = UiKit.Dp(6);
        _modeRow.AddView(doubleChip, doubleParams);

        _modeRow.AddView(new Space(Context!), new LinearLayout.LayoutParams(0, 1) { Weight = 1f });

        _rtlChip = ModeChip(_rtl ? "RTL" : "LTR", _rtl, () =>
        {
            _rtl = !_rtl;
            AppServices.Settings.MangaRightToLeft = _rtl;
            BuildModeRow();
        });
        _modeRow.AddView(_rtlChip);
    }

    // ---------------------------------------------------------------- 启动

    public async void Start()
    {
        _titleText.Text = _book.Title;
        BuildModeRow();
        if (_book.IsError)
        {
            ShowError(_book.Error ?? "文件无法读取");
            return;
        }

        try
        {
            if (_book.Type == BookType.Comic) await InitComicAsync();
            else await InitNovelAsync();
        }
        catch (Exception ex)
        {
            Log.Error("阅读器打开失败: " + _book.FilePath, ex);
            ShowError("打开失败：" + ex.Message);
        }
    }

    private async Task InitComicAsync()
    {
        ShowLoading("正在打开…");
        string path = _book.FilePath;
        _source = await Task.Run(() => ComicSourceFactory.Create(path));
        int prefetch = AppServices.Settings.LowPerformanceMode ? 1 : AppServices.Settings.PrefetchPages;
        _loader = new ComicImageLoader(_source, AppServices.Cache, prefetch);

        var progress = _fromStart ? null : AppServices.Library.GetProgress(_book.Id);
        _page = progress is null ? 0 : Math.Clamp(progress.PageIndex, 0, _loader.PageCount - 1);
        _spreadStart = _page;
        _webtoonFraction = progress?.ScrollOffset ?? 0;
        _zoomRelative = progress is { Zoom: > 0 } ? Math.Clamp(progress.Zoom, 0.2, 6.0) : 1.0;

        string savedMode = _fromStart ? "" : (progress?.Mode ?? "");
        string mode = savedMode is "paged" or "webtoon" or "double"
            ? savedMode
            : AppServices.Settings.DefaultComicMode;
        SwitchMode(mode);
        if (_fromStart) SaveNow();
    }

    private async Task InitNovelAsync()
    {
        if (_book.Format == BookFormat.Epub)
        {
            _epubZip = await Task.Run(() => new ZipArchive(
                new FileStream(_book.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                ZipArchiveMode.Read));
            _imageResolver = entry =>
            {
                var e = _epubZip!.GetEntry(entry);
                var ms = new MemoryStream();
                if (e is not null)
                {
                    using var s = e.Open();
                    s.CopyTo(ms);
                }
                ms.Position = 0;
                return ms;
            };
            var zip = _epubZip;
            _novel = await Task.Run(() => new EpubParser().ParseArchive(zip!, _book.Title));
        }
        else
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(_book.FilePath));
            var encoding = TextEncoding.Resolve(bytes, _encoding);
            _novel = await Task.Run(() => TxtParser.Parse(encoding.GetString(bytes), _book.Title));
        }

        if (_novel is null || _novel.Chapters.Count == 0)
        {
            ShowError("小说内容为空或无法解析");
            return;
        }

        _novelSettings = AppServices.Settings.NovelSettings.Clone();
        var progress = _fromStart ? null : AppServices.Library.GetProgress(_book.Id);
        _novelChapter = progress is null ? 0 : Math.Clamp(progress.ChapterIndex, 0, _novel.Chapters.Count - 1);
        _novelPage = progress?.PageIndex ?? 0;
        _novelScrollFraction = progress?.ScrollOffset ?? 0;
        string subMode = progress?.Mode == "scroll" ? "scroll" : "paged";

        _mode = "novel";
        _novelReady = true;
        _contentHost.Visibility = ViewStates.Visible;
        _pager.Visibility = _webtoon.Visibility = ViewStates.Gone;
        _novelView.Visibility = ViewStates.Visible;
        _loading.Visibility = ViewStates.Gone;
        _novelView.ApplySettings(_novelSettings, rebuild: false);
        _novelView.SetSubMode(subMode, keepPosition: false);
        BuildModeRow();
        BuildZoomRow();
        LoadNovelChapter(_novelChapter, resetPosition: false);
        if (_fromStart) SaveNow();
    }

    // ---------------------------------------------------------------- 漫画模式

    private void SwitchMode(string mode)
    {
        if (_loader is null) return;
        _mode = mode;
        AppServices.Settings.DefaultComicMode = mode == "webtoon" || mode == "double" ? mode : "paged";

        _pager.Visibility = mode is "paged" or "double" ? ViewStates.Visible : ViewStates.Gone;
        _webtoon.Visibility = mode == "webtoon" ? ViewStates.Visible : ViewStates.Gone;
        _novelView.Visibility = ViewStates.Gone;
        _loading.Visibility = ViewStates.Gone;

        BuildModeRow();
        BuildZoomRow();

        if (mode == "webtoon") _ = ShowWebtoonAsync();
        else if (mode == "double") RenderDoubleSpread();
        else LoadPageAsync(_page);
    }

    private async Task ShowWebtoonAsync()
    {
        if (_loader is null) return;
        if (_webtoonReady)
        {
            _webtoon.ScrollToPage(_page);
            UpdateComicInfo();
            return;
        }
        ShowLoading("正在排版条漫…");
        _webtoonReady = true;
        int start = _page;
        await _webtoon.InitializeAsync(_loader, i => _loader!.GetPageSize(i), start, (float)_zoomRelative);
        _loading.Visibility = ViewStates.Gone;
        if (_fromStart) _webtoon.ScrollToFraction(0);
        else _webtoon.ScrollToFraction(_webtoonFraction);
        _webtoon.Invalidate();
        UpdateComicInfo();
    }

    private void RenderDoubleSpread()
    {
        if (_loader is null) return;
        int count = _loader.PageCount;
        _spreadStart = Math.Clamp(_spreadStart, 0, count - 1);
        var dim = _loader.GetPageSize(_spreadStart);
        _doubleWide = dim is { Width: var w, Height: var h } && w > h;
        int right = _doubleWide || _spreadStart + 1 >= count ? -1 : _spreadStart + 1;

        var left = _loader.GetCached(_spreadStart);
        var rightBmp = right >= 0 ? _loader.GetCached(right) : null;
        _pager.SetPages(left, rightBmp);
        _pager.SetFitMode("page");

        _ = LoadSpreadAsync(_spreadStart, right);
    }

    private async Task LoadSpreadAsync(int leftIndex, int rightIndex)
    {
        if (_loader is null) return;
        var left = await _loader.GetPageAsync(leftIndex);
        if (_closed || _mode != "double" || leftIndex != _spreadStart) return;
        Bitmap? right = rightIndex >= 0 ? await _loader.GetPageAsync(rightIndex) : null;
        if (_closed || _mode != "double" || leftIndex != _spreadStart) return;
        _pager.SetPages(left, right);
        _pager.SetFitMode("page");
        _loader.Prefetch(leftIndex);
        UpdateComicInfo();
        ScheduleSave();
    }

    private async void LoadPageAsync(int index)
    {
        if (_loader is null || _closed) return;
        index = Math.Clamp(index, 0, _loader.PageCount - 1);

        _pageCts?.Cancel();
        var cts = _pageCts = new CancellationTokenSource();
        long version = ++_pageVersion;
        _page = index;
        ShowLoading("正在加载…");

        try
        {
            var bitmap = await _loader.GetPageAsync(index, cts.Token);
            if (_closed || _mode != "paged" || index != _page || version != _pageVersion) return;
            if (bitmap is null)
            {
                ShowLoading("加载失败，点击重试");
                return;
            }
            _loading.Visibility = ViewStates.Gone;
            _pager.SetPages(bitmap, null);
            _pager.SetFitMode(_fitMode, _zoomRelative);
            _loader.Prefetch(index);
            UpdateComicInfo();
            ScheduleSave();
        }
        catch (OperationCanceledException)
        {
            // 被更新的跳页取代
        }
        catch (Exception ex)
        {
            Log.Error($"加载第 {index + 1} 页失败", ex);
            if (_closed || _mode != "paged" || index != _page || version != _pageVersion) return;
            ShowLoading("加载失败，点击重试");
        }
    }

    private void UpdateComicInfo()
    {
        if (_loader is null) return;
        int count = _loader.PageCount;
        if (_mode == "double")
        {
            int right = _doubleWide || _spreadStart + 1 >= count ? -1 : _spreadStart + 1;
            _pageInfo.Text = right >= 0 ? $"{_spreadStart + 1}-{right + 1} / {count}" : $"{_spreadStart + 1} / {count}";
            SetSlider((double)(_spreadStart + 1) / count);
        }
        else if (_mode == "webtoon")
        {
            _pageInfo.Text = $"{_page + 1} / {count}";
            SetSlider((double)(_page + 1 + _webtoon.ScrollFraction) / count);
        }
        else
        {
            _pageInfo.Text = $"{_page + 1} / {count}";
            SetSlider((double)(_page + 1) / count);
        }
    }

    // ---------------------------------------------------------------- 翻页/缩放

    private void Prev()
    {
        if (_mode == "novel")
        {
            _novelView.PrevPage();
            return;
        }
        if (_loader is null) return;
        switch (_mode)
        {
            case "webtoon":
                _webtoon.ScrollToPage(Math.Max(0, _webtoon.CurrentPage - 1));
                break;
            case "double":
                _spreadStart = Math.Max(0, _spreadStart - (_doubleWide ? 1 : 2));
                RenderDoubleSpread();
                break;
            default:
                LoadPageAsync(_page - 1);
                break;
        }
    }

    private void Next()
    {
        if (_mode == "novel")
        {
            _novelView.NextPage();
            return;
        }
        if (_loader is null) return;
        switch (_mode)
        {
            case "webtoon":
                _webtoon.ScrollToPage(Math.Min(_loader.PageCount - 1, _webtoon.CurrentPage + 1));
                break;
            case "double":
                _spreadStart = Math.Min(_loader.PageCount - 1, _spreadStart + (_doubleWide ? 1 : 2));
                RenderDoubleSpread();
                break;
            default:
                LoadPageAsync(_page + 1);
                break;
        }
    }

    private void SetFit(string mode)
    {
        _fitMode = mode;
        _pager.SetFitMode(mode);
        BuildZoomRow();
        ScheduleSave();
    }

    private void StepZoom(double factor)
    {
        if (_mode == "webtoon")
        {
            _webtoon.SetScaleFactor(_webtoonScale * factor);
            return;
        }
        _pager.SetZoomRelative(_zoomRelative * factor);
        _zoomRelative = Math.Clamp(_zoomRelative * factor, 0.2, 6.0);
        BuildZoomRow();
        ScheduleSave();
    }

    private void SetSlider(double fraction)
    {
        _updatingSlider = true;
        _progressBar.Progress = (int)Math.Clamp(fraction * 1000, 0, 1000);
        _updatingSlider = false;
    }

    private void ApplySliderJump()
    {
        if (_updatingSlider) return;
        double fraction = _progressBar.Progress / 1000.0;
        if (_mode == "novel" && _novel is not null)
        {
            double per = 1.0 / _novel.Chapters.Count;
            int chapter = Math.Clamp((int)(fraction / per), 0, _novel.Chapters.Count - 1);
            if (chapter != _novelChapter) LoadNovelChapter(chapter);
            else if (_novelView.SubMode == "paged")
            {
                double inner = (fraction - chapter * per) / per;
                _novelView.SetPage((int)Math.Round(inner * Math.Max(0, _novelView.PageCount - 1)));
            }
            else
            {
                double inner = (fraction - chapter * per) / per;
                _novelView.SetScrollFraction(inner);
            }
            return;
        }
        if (_loader is null) return;
        int target = Math.Clamp((int)(fraction * _loader.PageCount), 0, _loader.PageCount - 1);
        if (_mode == "webtoon") _webtoon.ScrollToPage(target);
        else if (_mode == "double")
        {
            _spreadStart = target;
            RenderDoubleSpread();
        }
        else LoadPageAsync(target);
    }

    // ---------------------------------------------------------------- 小说

    private void LoadNovelChapter(int index, bool resetPosition = true)
    {
        if (_novel is null || _novel.Chapters.Count == 0) return;
        _novelChapter = Math.Clamp(index, 0, _novel.Chapters.Count - 1);
        var chapter = _novel.Chapters[_novelChapter];
        if (resetPosition)
        {
            _novelPage = 0;
            _novelScrollFraction = 0;
        }
        _novelView.Load(chapter, _imageResolver, _novelSettings);
        _titleText.Text = _book.Title + " · " + chapter.Title;
        if (!resetPosition && _novelView.SubMode == "scroll") _novelView.SetScrollFraction(_novelScrollFraction);
        ScheduleSave();
    }

    private void SetNovelSubMode(string mode)
    {
        _novelView.SetSubMode(mode);
        BuildZoomRow();
        ScheduleSave();
    }

    private void ShowChapters()
    {
        if (_novel is null) return;
        var titles = _novel.Chapters.Select((c, i) => $"{i + 1}. {c.Title}").ToList();
        var list = new ListView(Context!);
        var adapter = new ArrayAdapter<string>(Context!, Android.Resource.Layout.SimpleListItem1, titles);
        list.Adapter = adapter;
        var dialog = new AlertDialog.Builder(_activity)!
            .SetTitle("章节目录")!
            .SetView(list)!
            .SetNegativeButton("关闭", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .Create();
        list.ItemClick += (_, e) =>
        {
            dialog.Dismiss();
            LoadNovelChapter(e.Position);
        };
        dialog.Show();
    }

    private void ShowNovelSettings()
    {
        var dialog = new NovelSettingsDialog(_activity, _novelSettings, AppServices.Settings.AutoScrollSpeed,
            _book.Format != BookFormat.Epub, onChanged: (settings, rebuild) =>
            {
                _novelSettings = settings;
                _novelView.ApplySettings(settings, rebuild);
                BuildZoomRow();
            },
            onEncodingChanged: overrideEncoding =>
            {
                _encoding = overrideEncoding;
                _ = ReloadTxtAsync();
            });
        dialog.Show();
    }

    private async Task ReloadTxtAsync()
    {
        if (_book.Format == BookFormat.Epub) return;
        byte[] bytes = await Task.Run(() => File.ReadAllBytes(_book.FilePath));
        var encoding = TextEncoding.Resolve(bytes, _encoding);
        _novel = await Task.Run(() => TxtParser.Parse(encoding.GetString(bytes), _book.Title));
        _novelChapter = 0;
        _novelPage = 0;
        _novelScrollFraction = 0;
        LoadNovelChapter(0);
    }

    // ---------------------------------------------------------------- 书签

    private void ShowBookmarks()
    {
        var bookmarks = AppServices.Library.GetBookmarks(_book.Id);
        var container = new LinearLayout(Context!) { Orientation = Orientation.Vertical };
        container.SetPadding(UiKit.Dp(8), UiKit.Dp(8), UiKit.Dp(8), UiKit.Dp(8));

        var items = bookmarks.Select(bm => _book.Type == BookType.Comic
            ? $"第 {bm.PageIndex + 1} 页 · {bm.CreatedAt:MM-dd HH:mm}"
            : $"第 {bm.ChapterIndex + 1} 章 第 {bm.PageIndex + 1} 页 · {bm.CreatedAt:MM-dd HH:mm}").ToList();

        var dialog = new AlertDialog.Builder(_activity)!
            .SetTitle("书签")!
            .SetView(container)!
            .SetNegativeButton("关闭", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .Create();

        var addButton = new TextView(Context!)
        {
            Text = "＋ 添加当前页书签",
            Gravity = GravityFlags.Center
        };
        addButton.SetTextColor(Color.White);
        addButton.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        addButton.Background = UiKit.Round(AppTheme.AccentSolid, 8);
        addButton.SetPadding(UiKit.Dp(10), UiKit.Dp(9), UiKit.Dp(10), UiKit.Dp(9));
        addButton.Click += (_, _) =>
        {
            int page = _mode == "novel" ? _novelView.PageIndex : _mode == "double" ? _spreadStart : _page;
            int chapter = _mode == "novel" ? _novelChapter : 0;
            AppServices.Library.AddBookmark(new Bookmark { BookId = _book.Id, PageIndex = page, ChapterIndex = chapter });
            dialog.Dismiss();
            UiKit.Toast(Context!, "已添加书签");
            ShowBookmarks();
        };
        container.AddView(addButton);

        if (items.Count == 0)
        {
            var empty = UiKit.Text(Context!, "还没有书签", 12.5, AppTheme.TextFaint);
            empty.Gravity = GravityFlags.Center;
            empty.SetPadding(0, UiKit.Dp(18), 0, UiKit.Dp(18));
            container.AddView(empty);
        }
        else
        {
            var list = new ListView(Context!);
            list.Adapter = new ArrayAdapter<string>(Context!, Android.Resource.Layout.SimpleListItem1, items);
            list.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(240));
            list.ItemClick += (_, e) =>
            {
                var bm = bookmarks[e.Position];
                dialog.Dismiss();
                JumpToBookmark(bm);
            };
            list.ItemLongClick += (_, e) =>
            {
                AppServices.Library.DeleteBookmark(bookmarks[e.Position].Id);
                dialog.Dismiss();
                UiKit.Toast(Context!, "已删除书签");
                ShowBookmarks();
                e.Handled = true;
            };
            container.AddView(list);
            var hint = UiKit.Text(Context!, "长按条目可删除", 10.5, AppTheme.TextFaint);
            hint.SetPadding(0, UiKit.Dp(6), 0, 0);
            container.AddView(hint);
        }
        dialog.Show();
    }

    private void JumpToBookmark(Bookmark bookmark)
    {
        if (_book.Type == BookType.Comic)
        {
            if (_mode == "webtoon") _webtoon.ScrollToPage(bookmark.PageIndex);
            else if (_mode == "double")
            {
                _spreadStart = bookmark.PageIndex;
                RenderDoubleSpread();
            }
            else LoadPageAsync(bookmark.PageIndex);
        }
        else
        {
            LoadNovelChapter(bookmark.ChapterIndex);
            _novelView.SetPage(bookmark.PageIndex);
        }
    }

    // ---------------------------------------------------------------- 进度

    private void ScheduleSave()
    {
        if (_closed) return;
        _ui.RemoveCallbacks(_saveRunnable);
        _ui.PostDelayed(_saveRunnable, 2000);
    }

    private void SaveNow()
    {
        if (_closed || _book.Id <= 0) return;
        try
        {
            int page;
            int chapter = 0;
            double scroll = 0;
            double percent;
            string mode = _mode;
            double zoom = 0;

            if (_mode == "novel" && _novel is not null)
            {
                chapter = _novelChapter;
                page = _novelView.PageIndex;
                if (_novelView.SubMode == "scroll")
                {
                    scroll = _novelView.ScrollFraction;
                    _novelScrollFraction = scroll;
                }
                double fraction = _novelView.SubMode == "scroll"
                    ? scroll
                    : _novelView.PageCount <= 1 ? 0 : (double)page / (_novelView.PageCount - 1);
                percent = Math.Clamp((chapter + fraction) / Math.Max(1, _novel.Chapters.Count), 0, 1);
            }
            else if (_loader is not null)
            {
                if (_mode == "webtoon")
                {
                    page = _webtoon.CurrentPage;
                    scroll = _webtoon.ScrollFraction;
                    percent = Math.Clamp((double)(page + scroll + 1) / _loader.PageCount, 0, 1);
                    zoom = _webtoonScale;
                }
                else if (_mode == "double")
                {
                    page = _spreadStart;
                    percent = Math.Clamp((double)(page + 1) / _loader.PageCount, 0, 1);
                    zoom = _fitMode == "custom" ? _zoomRelative : 0;
                }
                else
                {
                    page = _page;
                    percent = Math.Clamp((double)(page + 1) / _loader.PageCount, 0, 1);
                    zoom = _fitMode == "custom" ? _zoomRelative : 0;
                }
            }
            else
            {
                return;
            }

            AppServices.Library.SaveProgress(_book.Id, percent, page, scroll, chapter, mode, zoom);
        }
        catch (Exception ex)
        {
            Log.Error("保存进度失败", ex);
        }
    }

    // ---------------------------------------------------------------- 通用

    private void ShowLoading(string text)
    {
        _loading.Text = text;
        _loading.Visibility = ViewStates.Visible;
    }

    private void ShowError(string message)
    {
        _pager.Visibility = _webtoon.Visibility = _novelView.Visibility = ViewStates.Gone;
        ShowLoading(message);
        _loading.SetTextColor(AppTheme.Danger);
    }

    private void ToggleBars()
    {
        _barsVisible = !_barsVisible;
        _topBar.Visibility = _barsVisible ? ViewStates.Visible : ViewStates.Gone;
        _bottomBar.Visibility = _barsVisible ? ViewStates.Visible : ViewStates.Gone;
    }

    public bool HandleBack()
    {
        if (!_barsVisible)
        {
            ToggleBars();
            return true;
        }
        if (_mode == "webtoon" && _page > 0)
        {
            _webtoon.ScrollToPage(0);
            return true;
        }
        return false;
    }

    public void Close()
    {
        SaveNow();
        _closed = true;
        _ui.RemoveCallbacks(_saveRunnable);
        _pageCts?.Cancel();
        _novelView.StopAutoScroll();
        _novelView.Release();
        _webtoon.Release();
        try { _loader?.Dispose(); } catch { }
        try { _epubZip?.Dispose(); } catch { }
    }

    public void RefreshTheme()
    {
        _themeChip.Text = AppTheme.Dark ? "☀" : "☾";
        SetBackgroundColor(AppTheme.ReaderBg);
        _topBar.SetBackgroundColor(AppTheme.WindowBg);
        _bottomBar.SetBackgroundColor(AppTheme.WindowBg);
        _novelSettings.TextColor = AppTheme.Dark ? "#E8E8F4" : "#2F2A3E";
        _novelSettings.Background = AppTheme.Dark ? "#1A1A2E" : "#FAF6F8";
        _novelView.ApplySettings(_novelSettings, rebuild: _novelReady);
    }

    private void Ui(Action action)
    {
        if (Android.OS.Looper.MyLooper() == Android.OS.Looper.MainLooper) action();
        else _activity.RunOnUiThread(action);
    }
}
