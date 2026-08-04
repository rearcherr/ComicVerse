using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ComicVerse.App.Helpers;
using ComicVerse.Core;
using ComicVerse.Core.Comics;
using ComicVerse.Core.Models;
using ComicVerse.Core.Novel;
using ComicVerse.Core.Services;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow : Window
{
    private readonly Book _book;
    private readonly bool _fromStart;

    private IComicSource? _source;
    private ComicImageLoader? _loader;
    private NovelBook? _novel;
    private ZipArchive? _epubZip;
    private Func<string, Stream>? _imageResolver;

    private string _mode = "paged";
    private string _fitMode = "width";
    private double _zoom = 1.0;
    private int _page;
    private int _spreadStart;
    private bool _doubleWide;
    private bool _rtl;
    private bool _webtoonInitialized;
    private double _webtoonFraction;

    private int _novelChapter;
    private int _novelPage;
    private double _novelScrollFraction;
    private string _novelSubMode = "paged";
    private readonly Dictionary<int, int> _chapterPages = new();
    private NovelViewSettings _novelSettings = new();
    private Encoding? _txtEncoding;

    private readonly DispatcherTimer _saveTimer;
    private DispatcherTimer? _rebuildTimer;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _hideBarsTimer;
    private bool _closed;
    private bool _updatingSlider;
    private bool _sliderActive;
    private bool _changingChapter;
    private bool _barsVisible = true;
    private CancellationTokenSource? _pageLoadCts;
    private long _pageLoadVersion;

    public ReaderWindow(Book book, bool fromStart = false)
    {
        _book = book;
        _fromStart = fromStart;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => SaveNow();

        _rtl = App.Settings.MangaRightToLeft;
        UpdateRtlGlyph();
        ReaderThemeGlyph.Text = ThemeService.Current == "dark" ? "☾" : "☀";
    }

    // 自检钩子（仅在 --smoke 模式下使用）
    internal bool IsComicPageLoaded => _mode == "paged" && PageImage.Source is not null;
    internal bool IsWebtoonReady => _webtoonInitialized && WebtoonView.IsReady && WebtoonView.PageCount > 0;
    internal int WebtoonPageCount => WebtoonView.PageCount;
    internal bool IsNovelReady => _novel is not null && NovelPaged.Document is not null;
    internal bool IsDoubleReady => _mode == "double" && (LeftImage.Source is not null || RightImage.Source is not null);
    internal string WebtoonStats => $"canvas={WebtoonView.CanvasWidth:F0}x{WebtoonView.CanvasHeight:F0} rendered={WebtoonView.RenderedCount} scale={_zoom:F2}";
    internal void TestSwitchToWebtoon() => ModeWebtoon.IsChecked = true;
    internal void TestSwitchToDouble() => ModeDouble.IsChecked = true;
    internal void TestSwitchToPaged() => ModePaged.IsChecked = true;
    internal void TestNext() => Next();
    internal void TestWebtoonScrollBy(double dy) => WebtoonView.ScrollBy(dy);
    internal void TestJumpToPage(int page) => LoadPageAsync(page);
    internal void TestWebtoonJumpTo(int page) => WebtoonView.ScrollToPage(page);
    internal int WebtoonRenderedCount => WebtoonView.RenderedCount;
    internal int WebtoonRenderedLoadedCount => WebtoonView.RenderedWithSourceCount;
    internal int CurrentPageNumber => _page;
    internal int ComicPageCount => _loader?.PageCount ?? 0;
    internal void TestSetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.2, 3.0);
        _fitMode = "custom";
        ZoomSlider.Value = _zoom * 100;
    }
    internal double CurrentZoom => _mode == "webtoon" ? _zoom : PageScale.ScaleX;
    internal string ZoomTextValue => ZoomText.Text;
    private bool _syncingZoom;
    internal int NovelPage => _novelPage;
    internal int NovelChapterIndex => _novelChapter;
    internal void TestNovelNextPage() => NovelNext();
    internal void TestNovelNextChapter()
    {
        if (_novel is not null && _novelChapter < _novel.Chapters.Count - 1)
            LoadNovelChapter(_novelChapter + 1);
    }
    internal void TestToggleTheme() => Theme_Click(this, new RoutedEventArgs());
    internal string NovelBackgroundHex =>
        (NovelPaged.Document as System.Windows.Documents.FlowDocument)?.Background is System.Windows.Media.SolidColorBrush b
            ? b.Color.ToString()
            : "";

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TitleText.Text = _book.Title;
        TypeBadgeText.Text = _book.Type == BookType.Comic ? "漫画" : "小说";

        if (_book.IsError)
        {
            ShowError(_book.Error ?? "文件无法读取");
            return;
        }

        try
        {
            if (_book.Type == BookType.Comic)
                await InitComicAsync();
            else
                await InitNovelAsync();
        }
        catch (Exception ex)
        {
            Log.Error("阅读器打开失败: " + _book.FilePath, ex);
            ShowError("打开失败：" + ex.Message);
        }
    }

    private async Task InitComicAsync()
    {
        _source = ComicSourceFactory.Create(_book.FilePath);
        int prefetch = App.Settings.LowPerformanceMode ? 1 : App.Settings.PrefetchPages;
        _loader = new ComicImageLoader(_source, App.SharedCache, prefetch);

        ModePaged.Visibility = ModeWebtoon.Visibility = ModeDouble.Visibility = Visibility.Visible;
        RtlButton.Visibility = Visibility.Visible;
        ZoomOutButton.Visibility = ZoomInButton.Visibility = ZoomSlider.Visibility =
            ZoomText.Visibility = FitWidthButton.Visibility = FitPageButton.Visibility = ActualButton.Visibility = Visibility.Visible;

        var prog = _fromStart ? null : App.Library.GetProgress(_book.Id);
        _page = prog is null ? 0 : Math.Clamp(prog.PageIndex, 0, _loader.PageCount - 1);
        _webtoonFraction = prog?.ScrollOffset ?? 0;
        if (prog is { Zoom: > 0 })
        {
            _zoom = Math.Clamp(prog.Zoom, 0.2, 3.0);
            _fitMode = "custom";
            ZoomSlider.Value = _zoom * 100;
            UpdateZoomText(_zoom);
        }

        string savedMode = _fromStart ? "" : (prog?.Mode ?? "");
        if (savedMode == "webtoon")
        {
            ModeWebtoon.IsChecked = true;
        }
        else if (savedMode == "double")
        {
            ModeDouble.IsChecked = true;
        }
        else if (savedMode == "paged")
        {
            ModePaged.IsChecked = true;
            ShowComicPaged(_page);
        }
        else
        {
            switch (App.Settings.DefaultComicMode)
            {
                case "webtoon":
                    ModeWebtoon.IsChecked = true;
                    break;
                case "double":
                    ModeDouble.IsChecked = true;
                    break;
                default:
                    ModePaged.IsChecked = true;
                    ShowComicPaged(_page);
                    break;
            }
        }
        if (_fromStart) SaveNow();
    }

    private async Task InitNovelAsync()
    {
        ModePaged.Visibility = ModeWebtoon.Visibility = ModeDouble.Visibility = Visibility.Collapsed;
        RtlButton.Visibility = Visibility.Collapsed;
        ZoomOutButton.Visibility = ZoomInButton.Visibility = ZoomSlider.Visibility =
            ZoomText.Visibility = FitWidthButton.Visibility = FitPageButton.Visibility = ActualButton.Visibility = Visibility.Collapsed;
        ChaptersButton.Visibility = SettingsPanelButton.Visibility = Visibility.Visible;

        if (_book.Format == BookFormat.Epub)
        {
            _epubZip = new ZipArchive(new FileStream(_book.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ZipArchiveMode.Read);
            _imageResolver = entry =>
            {
                var e = _epubZip.GetEntry(entry);
                if (e is null) return new MemoryStream();
                var ms = new MemoryStream();
                using var s = e.Open();
                s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            };
            _novel = await Task.Run(() => new EpubParser().ParseArchive(_epubZip, _book.Title));
        }
        else
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(_book.FilePath));
            var enc = _txtEncoding ?? EncodingDetector.Detect(bytes);
            string text = enc.GetString(bytes);
            _novel = await Task.Run(() => TxtParser.Parse(text, _book.Title));
        }

        if (_novel!.Chapters.Count == 0)
        {
            ShowError("小说内容为空或无法解析");
            return;
        }

        _novelSettings = App.Settings.NovelSettings.Clone();
        ChapterList.ItemsSource = _novel.Chapters.Select((c, i) => $"{i + 1}. {c.Title}").ToList();
        LoadNovelSettingsUi();
        ApplyThemeToNovelSettings(rebuild: false);

        var prog = _fromStart ? null : App.Library.GetProgress(_book.Id);
        int chapter = prog is null ? 0 : Math.Clamp(prog.ChapterIndex, 0, _novel.Chapters.Count - 1);
        _novelPage = prog?.PageIndex ?? 0;
        _novelScrollFraction = prog?.ScrollOffset ?? 0;
        _novelSubMode = "paged";
        NovelModePaged.IsChecked = true;
        NovelView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = WebtoonView.Visibility = DoubleView.Visibility = Visibility.Collapsed;
        _mode = "novel";
        ApplyNovelSubMode();
        LoadNovelChapter(chapter, resetPosition: false);
        if (_fromStart) SaveNow();
    }

    #region 漫画：翻页

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var rb = (RadioButton)sender;
        if (rb.IsChecked != true) return;
        switch ((string)rb.Tag)
        {
            case "webtoon": ShowWebtoon(); break;
            case "double": ShowDouble(); break;
            default: ShowComicPaged(_page); break;
        }
    }

    private void ShowComicPaged(int index)
    {
        _mode = "paged";
        ComicPagedView.Visibility = Visibility.Visible;
        WebtoonView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        LoadPageAsync(index);
    }

    private async void LoadPageAsync(int index)
    {
        if (_loader is null || _closed) return;
        index = Math.Clamp(index, 0, _loader.PageCount - 1);

        // 快速跳页时取消上一次未完成的加载，避免解码任务堆积拖慢目标页
        _pageLoadCts?.Cancel();
        var cts = _pageLoadCts = new CancellationTokenSource();
        long version = ++_pageLoadVersion;
        _page = index;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "正在加载…";

        try
        {
            var bmp = await _loader.GetPageAsync(index, cts.Token);
            if (_closed || _mode != "paged" || index != _page || version != _pageLoadVersion) return;
            if (bmp is null)
            {
                // 解码失败：保留旧页，给出可重试提示，而不是静默白屏
                LoadingText.Text = "加载失败，点击重试";
                return;
            }

            PageImage.Source = bmp;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ApplyFit();
            PageImage.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(180)));
            _loader.Prefetch(index);
            UpdateComicInfo();
            ScheduleSave();
        }
        catch (OperationCanceledException)
        {
            // 被更新的跳页或关闭操作取代，无需处理
        }
        catch (Exception ex)
        {
            Log.Error($"加载第 {index + 1} 页失败", ex);
            if (_closed || _mode != "paged" || index != _page || version != _pageLoadVersion) return;
            LoadingText.Text = "加载失败，点击重试";
        }
    }

    private void LoadingPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loader is not null && !_closed)
            LoadPageAsync(_page);
    }

    private void ApplyFit()
    {
        if (PageImage.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;
        double vw = Math.Max(40, PageScroll.ViewportWidth);
        double vh = Math.Max(40, PageScroll.ViewportHeight);
        double sw = bmp.PixelWidth;
        double sh = bmp.PixelHeight;
        double scale = _fitMode switch
        {
            "height" => vh / sh,
            "page" => Math.Min(vw / sw, vh / sh),
            "actual" => 1.0,
            "custom" => _zoom,
            _ => vw / sw
        };
        scale = Math.Clamp(scale, 0.05, 8.0);
        PageScale.ScaleX = scale;
        PageScale.ScaleY = scale;
        UpdateZoomText(scale);
    }

    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mode == "paged" && PageImage.Source is not null)
            ApplyFit();
    }

    private void ZoneLeft_Click(object sender, MouseButtonEventArgs e)
    {
        if (_rtl) Next(); else Prev();
    }

    private void ZoneRight_Click(object sender, MouseButtonEventArgs e)
    {
        if (_rtl) Prev(); else Next();
    }

    private void ZoneMiddle_Click(object sender, MouseButtonEventArgs e) => ToggleBars();

    private void Prev_Click(object sender, RoutedEventArgs e) => Prev();

    private void Next_Click(object sender, RoutedEventArgs e) => Next();

    private void Prev()
    {
        if (_mode == "novel")
        {
            NovelPrev();
            return;
        }
        switch (_mode)
        {
            case "webtoon":
                WebtoonView.ScrollToPage(Math.Max(0, WebtoonView.CurrentPage - 1));
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
            NovelNext();
            return;
        }
        switch (_mode)
        {
            case "webtoon":
                WebtoonView.ScrollToPage(Math.Min(_loader!.PageCount - 1, WebtoonView.CurrentPage + 1));
                break;
            case "double":
                _spreadStart = Math.Min(_loader!.PageCount - 1, _spreadStart + (_doubleWide ? 1 : 2));
                RenderDoubleSpread();
                break;
            default:
                LoadPageAsync(_page + 1);
                break;
        }
    }

    private void UpdateComicInfo()
    {
        if (_loader is null) return;
        int count = _loader.PageCount;
        string info;
        if (_mode == "double")
        {
            int right = _doubleWide || _spreadStart + 1 >= count ? -1 : _spreadStart + 1;
            info = right >= 0 ? $"{_spreadStart + 1}-{right + 1} / {count}" : $"{_spreadStart + 1} / {count}";
        }
        else
        {
            info = $"{_page + 1} / {count}";
        }
        PageInfoText.Text = info;
        SetSlider((double)(_page + 1) / count);
    }

    #endregion

    #region 漫画：条漫

    private async void ShowWebtoon()
    {
        _mode = "webtoon";
        _pageLoadCts?.Cancel();
        WebtoonView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;

        if (!_webtoonInitialized)
        {
            _webtoonInitialized = true;
            WebtoonView.ScaleChanged += OnWebtoonScaleChanged;
            WebtoonView.CurrentPageChanged += idx =>
            {
                if (_mode != "webtoon") return; // 条漫不可见时忽略滚动事件，避免覆盖翻页/双页的页码
                _page = idx;
                PageInfoText.Text = $"{idx + 1} / {_loader!.PageCount}";
                SetSlider((double)(idx + 1 + WebtoonView.ScrollFraction) / _loader.PageCount);
                ScheduleSave();
            };
            double scale = _zoom;
            int start = _page;
            await WebtoonView.InitializeAsync(_loader!, i => _loader!.GetPageSize(i), start, scale);
            if (_closed || _mode != "webtoon") return;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                WebtoonView.EnsureLayout();
                WebtoonView.ScrollToFraction(_fromStart ? 0 : _webtoonFraction);
                UpdateComicInfo();
            });
        }
    }

    #endregion

    #region 漫画：双页

    private void ShowDouble()
    {
        _mode = "double";
        _pageLoadCts?.Cancel();
        DoubleView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = WebtoonView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        _spreadStart = Math.Clamp(_page, 0, _loader!.PageCount - 1);
        RenderDoubleSpread();
    }

    private async void RenderDoubleSpread()
    {
        if (_loader is null || _closed) return;
        int count = _loader.PageCount;
        _spreadStart = Math.Clamp(_spreadStart, 0, count - 1);
        DoubleLoading.Visibility = Visibility.Visible;
        LeftImage.Source = null;
        RightImage.Source = null;

        var dim = await Task.Run(() => _loader.GetPageSize(_spreadStart));
        _doubleWide = dim is { Width: var w, Height: var h } && w > h;
        int right = _doubleWide || _spreadStart + 1 >= count ? -1 : _spreadStart + 1;
        RightScroll.Visibility = right >= 0 ? Visibility.Visible : Visibility.Collapsed;

        var leftBmp = await _loader.GetPageAsync(_spreadStart);
        if (_closed || _mode != "double") return;
        LeftImage.Source = leftBmp;
        FitDoubleImage(LeftImage, leftBmp);

        if (right >= 0)
        {
            var rightBmp = await _loader.GetPageAsync(right);
            if (_closed || _mode != "double") return;
            RightImage.Source = rightBmp;
            FitDoubleImage(RightImage, rightBmp);
        }

        DoubleLoading.Visibility = Visibility.Collapsed;
        UpdateZoomText(_zoom);
        _loader.Prefetch(_spreadStart);
        UpdateComicInfo();
        ScheduleSave();
    }

    private void FitDoubleImage(Image image, System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        if (bmp is null) return;
        double targetW = image.Parent is ScrollViewer sv ? Math.Max(50, sv.ViewportWidth) : 800;
        image.Width = targetW * _zoom;
        image.Height = bmp.PixelHeight * (image.Width / Math.Max(1, bmp.PixelWidth));
    }

    #endregion

    #region 缩放

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "width";
        ApplyFit();
    }

    private void FitPage_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "page";
        ApplyFit();
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "actual";
        ApplyFit();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * 1.25, 0.2, 3.0);
        _fitMode = "custom";
        ZoomSlider.Value = _zoom * 100;
        ApplyFit();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Clamp(_zoom / 1.25, 0.2, 3.0);
        _fitMode = "custom";
        ZoomSlider.Value = _zoom * 100;
        ApplyFit();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoom) return;
        if (!IsLoaded) return;
        _zoom = Math.Clamp(e.NewValue / 100.0, 0.2, 3.0);
        _fitMode = "custom";
        if (_mode == "webtoon")
        {
            WebtoonView.ScaleFactor = _zoom;
            UpdateZoomText(_zoom);
        }
        else
        {
            ApplyFit();
        }
    }

    private void OnWebtoonScaleChanged(double scale)
    {
        if (_syncingZoom) return;
        _syncingZoom = true;
        try
        {
            _zoom = scale;
            ZoomText.Text = (scale * 100).ToString("0") + "%";
            if (Math.Abs(ZoomSlider.Value - scale * 100) > 0.5)
                ZoomSlider.Value = scale * 100;
        }
        finally
        {
            _syncingZoom = false;
        }
    }

    private void UpdateZoomText(double scale) => ZoomText.Text = (scale * 100).ToString("0") + "%";

    private void Rtl_Click(object sender, RoutedEventArgs e)
    {
        _rtl = !_rtl;
        App.Settings.MangaRightToLeft = _rtl;
        UpdateRtlGlyph();
    }

    private void UpdateRtlGlyph()
    {
        RtlGlyph.Opacity = _rtl ? 1.0 : 0.35;
        RtlGlyph.Text = _rtl ? "⇋" : "⇄";
    }

    #endregion

    #region 小说

    private void LoadNovelChapter(int index, bool resetPosition = true)
    {
        if (_novel is null) return;
        _novelChapter = Math.Clamp(index, 0, _novel.Chapters.Count - 1);
        var chapter = _novel.Chapters[_novelChapter];
        var builder = new NovelDocumentBuilder(_imageResolver);
        var doc = builder.Build(chapter, _novelSettings);
        NovelPaged.Document = null;
        NovelScroll.Document = null;
        NovelPaged.Document = doc;
        if (_novelSubMode == "scroll")
        {
            NovelPaged.Document = null;
            NovelScroll.Document = doc;
        }
        if (resetPosition)
        {
            _novelPage = 0;
            _novelScrollFraction = 0;
        }

        _changingChapter = true;
        if (ChapterList.Items.Count > _novelChapter)
            ChapterList.SelectedIndex = _novelChapter;
        _changingChapter = false;

        TitleText.Text = _book.Title + " · " + chapter.Title;
        UpdateNovelInfo();
        ScheduleSave();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            int pages = ComputeChapterPages();
            _chapterPages[_novelChapter] = pages;
            NovelPaged.GoToPage(Math.Clamp(_novelPage, 0, pages - 1));
            if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                if (sv is not null)
                    sv.ScrollToVerticalOffset(_novelScrollFraction * Math.Max(0, sv.ScrollableHeight));
            }
            UpdateNovelInfo();
        });
    }

    private int ComputeChapterPages()
    {
        int pages = NovelPaged.PageCount;
        return pages > 0 ? pages : 1;
    }

    private void ApplyNovelSubMode()
    {
        var doc = (NovelPaged.Document ?? NovelScroll.Document) as FlowDocument;
        NovelPaged.Document = null;
        NovelScroll.Document = null;
        if (_novelSubMode == "scroll")
        {
            NovelScroll.Visibility = Visibility.Visible;
            NovelPaged.Visibility = Visibility.Collapsed;
            if (doc is not null) NovelScroll.Document = doc;
            if (_novel is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                    if (sv is not null)
                        sv.ScrollToVerticalOffset(_novelScrollFraction * Math.Max(0, sv.ScrollableHeight));
                    UpdateNovelInfo();
                });
            }
        }
        else
        {
            NovelPaged.Visibility = Visibility.Visible;
            NovelScroll.Visibility = Visibility.Collapsed;
            if (doc is not null) NovelPaged.Document = doc;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                int pages = _chapterPages.GetValueOrDefault(_novelChapter, 1);
                NovelPaged.GoToPage(Math.Clamp(_novelPage, 0, pages - 1));
                UpdateNovelInfo();
            });
        }
        StopAutoScroll();
    }

    private void NovelMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (NovelModeScroll.IsChecked == true) _novelSubMode = "scroll";
        else _novelSubMode = "paged";
        ApplyNovelSubMode();
        ScheduleSave();
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingChapter || _novel is null || ChapterList.SelectedIndex < 0) return;
        LoadNovelChapter(ChapterList.SelectedIndex);
    }

    private void ChaptersButton_Click(object sender, RoutedEventArgs e)
    {
        ChapterPanel.Visibility = ChapterPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SettingsPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NovelPrev()
    {
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset - sv.ViewportHeight * 0.9);
            return;
        }
        _novelPage = Math.Max(0, _novelPage - 1);
        NovelPaged.GoToPage(_novelPage);
        UpdateNovelInfo();
        ScheduleSave();
    }

    private void NovelNext()
    {
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset + sv.ViewportHeight * 0.9);
            return;
        }
        int pages = _chapterPages.GetValueOrDefault(_novelChapter, 1);
        if (_novelPage < pages - 1 || _novelChapter < _novel!.Chapters.Count - 1)
        {
            if (_novelPage < pages - 1)
            {
                _novelPage++;
                NovelPaged.GoToPage(_novelPage);
            }
            else
            {
                _novelPage = 0;
                LoadNovelChapter(_novelChapter + 1);
                return;
            }
            UpdateNovelInfo();
            ScheduleSave();
        }
    }

    private void UpdateNovelInfo()
    {
        if (_novel is null) return;
        int chapters = _novel.Chapters.Count;
        double fraction;
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            _novelScrollFraction = sv is null || sv.ScrollableHeight <= 0 ? 0 : sv.VerticalOffset / sv.ScrollableHeight;
            fraction = _novelScrollFraction;
        }
        else
        {
            int pages = _chapterPages.GetValueOrDefault(_novelChapter, 1);
            fraction = pages <= 1 ? 0 : (double)_novelPage / Math.Max(1, pages - 1);
        }
        PageInfoText.Text = $"第 {_novelChapter + 1}/{chapters} 章 · 第 {_novelPage + 1} 页";
        SetSlider((_novelChapter + fraction) / chapters);
        ScheduleSave();
    }

    private void LoadNovelSettingsUi()
    {
        var fonts = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(200)
            .ToList();
        FontCombo.ItemsSource = fonts;
        FontCombo.SelectedItem = fonts.FirstOrDefault(f => f.Contains(_novelSettings.FontFamily, StringComparison.OrdinalIgnoreCase))
                                 ?? fonts.FirstOrDefault(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase));

        FontSizeSlider.Value = _novelSettings.FontSize;
        LineSpacingSlider.Value = _novelSettings.LineSpacing;
        ParagraphSpacingSlider.Value = _novelSettings.ParagraphSpacing;
        MarginSlider.Value = _novelSettings.PageMargin;
        SelectComboByTag(TextColorCombo, _novelSettings.TextColor);
        SelectComboByTag(BgColorCombo, _novelSettings.Background);
        EncodingCombo.SelectedIndex = 0;
        AutoSpeedSlider.Value = 60;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void NovelSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        (_rebuildTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) }).Stop();
        _rebuildTimer.Tick -= RebuildNovel_Tick;
        _rebuildTimer.Tick += RebuildNovel_Tick;
        _rebuildTimer.Start();
    }

    private void NovelSettingSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        NovelSetting_Changed(sender, new RoutedEventArgs());
    }

    private void RebuildNovel_Tick(object? sender, EventArgs e)
    {
        if (_rebuildTimer is not null) _rebuildTimer.Stop();
        ReadNovelSettingsFromUi();
        RebuildNovelDocumentNow();
    }

    private void ReadNovelSettingsFromUi()
    {
        _novelSettings.FontFamily = FontCombo.SelectedItem as string ?? _novelSettings.FontFamily;
        _novelSettings.FontSize = FontSizeSlider.Value;
        _novelSettings.LineSpacing = LineSpacingSlider.Value;
        _novelSettings.ParagraphSpacing = ParagraphSpacingSlider.Value;
        _novelSettings.PageMargin = MarginSlider.Value;
        _novelSettings.TextColor = (TextColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? _novelSettings.TextColor;
        _novelSettings.Background = (BgColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? _novelSettings.Background;
        App.Settings.NovelSettings = _novelSettings;
    }

    private void RebuildNovelDocumentNow()
    {
        double fraction = _novelSubMode == "scroll" ? _novelScrollFraction
            : _chapterPages.TryGetValue(_novelChapter, out int pc) && pc > 1 ? (double)_novelPage / (pc - 1) : 0;

        var builder = new NovelDocumentBuilder(_imageResolver);
        var doc = builder.Build(_novel!.Chapters[_novelChapter], _novelSettings);
        NovelPaged.Document = null;
        NovelScroll.Document = null;
        NovelPaged.Document = doc;
        if (_novelSubMode == "scroll")
        {
            NovelPaged.Document = null;
            NovelScroll.Document = doc;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            int pages = ComputeChapterPages();
            _chapterPages[_novelChapter] = pages;
            if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                if (sv is not null)
                    sv.ScrollToVerticalOffset(fraction * Math.Max(0, sv.ScrollableHeight));
            }
            else
            {
                _novelPage = Math.Min(pages - 1, (int)Math.Round(fraction * Math.Max(0, pages - 1)));
                NovelPaged.GoToPage(_novelPage);
            }
            UpdateNovelInfo();
        });
    }

    private void ApplyThemeToNovelSettings(bool rebuild = true)
    {
        if (_novel is null) return;
        bool light = ThemeService.Current == "light";
        const string darkBg = "#1A1A2E";
        const string darkText = "#E8E8F4";
        const string lightBg = "#FAF6F8";
        const string lightText = "#2F2A3E";
        if (_novelSettings.Background == (light ? darkBg : lightBg))
            _novelSettings.Background = light ? lightBg : darkBg;
        if (_novelSettings.TextColor == (light ? darkText : lightText))
            _novelSettings.TextColor = light ? lightText : darkText;
        SelectComboByTag(BgColorCombo, _novelSettings.Background);
        SelectComboByTag(TextColorCombo, _novelSettings.TextColor);
        App.Settings.NovelSettings = _novelSettings;
        if (rebuild)
            RebuildNovelDocumentNow();
    }

    private void Encoding_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _book.Format == BookFormat.Epub || EncodingCombo.SelectedIndex < 0) return;
        _txtEncoding = EncodingCombo.SelectedIndex switch
        {
            1 => new UTF8Encoding(false),
            2 => EncodingDetector.Gbk,
            3 => EncodingDetector.Big5,
            4 => EncodingDetector.ShiftJis,
            _ => null
        };
        _ = ReloadTxtAsync();
    }

    private async Task ReloadTxtAsync()
    {
        byte[] bytes = await Task.Run(() => File.ReadAllBytes(_book.FilePath));
        var enc = _txtEncoding ?? EncodingDetector.Detect(bytes);
        _novel = TxtParser.Parse(enc.GetString(bytes), _book.Title);
        ChapterList.ItemsSource = _novel.Chapters.Select((c, i) => $"{i + 1}. {c.Title}").ToList();
        _chapterPages.Clear();
        LoadNovelChapter(0);
    }

    private void AutoScroll_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AutoScrollToggle.IsChecked == true && _novelSubMode == "scroll")
        {
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _autoScrollTimer.Tick += (_, _) =>
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                if (sv is null) return;
                sv.ScrollToVerticalOffset(sv.VerticalOffset + AutoSpeedSlider.Value * 0.08);
                UpdateNovelInfo();
            };
            _autoScrollTimer.Start();
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
        if (AutoScrollToggle is not null && AutoScrollToggle.IsChecked == true)
            AutoScrollToggle.IsChecked = false;
    }

    private void NovelScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        StopAutoScroll();
    }

    #endregion

    #region 书签与进度

    private void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        BookmarkPanel.Visibility = BookmarkPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (BookmarkPanel.Visibility == Visibility.Visible)
            RefreshBookmarks();
    }

    private void BookmarkPanelClose_Click(object sender, RoutedEventArgs e) => BookmarkPanel.Visibility = Visibility.Collapsed;

    private void RefreshBookmarks()
    {
        var items = App.Library.GetBookmarks(_book.Id).Select(bm =>
        {
            string display = _book.Type == BookType.Comic
                ? $"第 {bm.PageIndex + 1} 页"
                : $"第 {bm.ChapterIndex + 1} 章 · 第 {bm.PageIndex + 1} 页";
            return new BookmarkItem(bm, display, bm.CreatedAt.ToString("MM-dd HH:mm"));
        }).ToList();
        BookmarkList.ItemsSource = items;
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        int page = _mode == "novel" ? _novelPage : _page;
        int chapter = _mode == "novel" ? _novelChapter : 0;
        App.Library.AddBookmark(new Bookmark { BookId = _book.Id, PageIndex = page, ChapterIndex = chapter });
        RefreshBookmarks();
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BookmarkItem item)
        {
            App.Library.DeleteBookmark(item.Bookmark.Id);
            RefreshBookmarks();
        }
    }

    private void BookmarkList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkItem item) return;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        if (_book.Type == BookType.Comic)
        {
            ModePaged.IsChecked = true;
            LoadPageAsync(item.Bookmark.PageIndex);
        }
        else
        {
            LoadNovelChapter(item.Bookmark.ChapterIndex);
            _novelPage = item.Bookmark.PageIndex;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_novelSubMode == "paged")
                    NovelPaged.GoToPage(_novelPage);
            });
        }
    }

    private void ScheduleSave()
    {
        if (_closed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_closed || _book.Id <= 0) return;
        int page = _page;
        int chapter = 0;
        double scroll = 0;
        double percent;

        if (_mode == "novel" && _novel is not null)
        {
            chapter = _novelChapter;
            page = _novelPage;
            if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                scroll = sv is null || sv.ScrollableHeight <= 0 ? _novelScrollFraction : sv.VerticalOffset / sv.ScrollableHeight;
            }
            double fraction = _novelSubMode == "scroll" ? scroll
                : _chapterPages.TryGetValue(_novelChapter, out int pc) && pc > 1 ? (double)_novelPage / (pc - 1) : 0;
            percent = Math.Clamp((_novelChapter + fraction) / _novel.Chapters.Count, 0, 1);
        }
        else if (_loader is not null)
        {
            page = _mode == "double" ? _spreadStart : _page;
            if (_mode == "webtoon")
            {
                scroll = WebtoonView.ScrollFraction;
                page = WebtoonView.CurrentPage;
                percent = Math.Clamp((double)(page + scroll + 1) / _loader.PageCount, 0, 1);
            }
            else
            {
                percent = Math.Clamp((double)(page + 1) / _loader.PageCount, 0, 1);
            }
        }
        else
        {
            percent = _book.Progress;
        }

        string mode = _mode;
        double zoom = 0;
        if (_mode == "webtoon") zoom = _zoom;
        else if (_mode is "paged" or "double") zoom = Math.Round(PageScale.ScaleX, 3);

        try
        {
            App.Library.SaveProgress(_book.Id, percent, page, scroll, chapter, mode, zoom);
        }
        catch (Exception ex)
        {
            Log.Error("保存进度失败", ex);
        }
    }

    private void SetSlider(double fraction)
    {
        _updatingSlider = true;
        ProgressSlider.Value = Math.Clamp(fraction, 0, 1) * 1000;
        _updatingSlider = false;
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _sliderActive || !IsLoaded) return;
        ApplySliderJump();
    }

    private void ApplySliderJump()
    {
        if (_updatingSlider || !IsLoaded) return;
        double fraction = ProgressSlider.Value / 1000.0;
        if (_mode == "novel")
        {
            int chapter = (int)(fraction * _novel!.Chapters.Count);
            if (chapter != _novelChapter) LoadNovelChapter(chapter);
            else if (_novelSubMode == "paged")
            {
                int pages = _chapterPages.GetValueOrDefault(_novelChapter, 1);
                _novelPage = Math.Clamp((int)(fraction * _novel.Chapters.Count * pages - _novelChapter * pages), 0, pages - 1);
                NovelPaged.GoToPage(_novelPage);
                UpdateNovelInfo();
            }
        }
        else if (_loader is not null)
        {
            int target = (int)(fraction * _loader.PageCount);
            target = Math.Clamp(target, 0, _loader.PageCount - 1);
            if (_mode == "webtoon")
            {
                _page = target;
                WebtoonView.ScrollToPage(target);
            }
            else if (_mode == "double")
            {
                _spreadStart = target;
                RenderDoubleSpread();
            }
            else LoadPageAsync(target);
        }
    }

    #endregion

    #region 通用

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 先保存（含模式/缩放），再标记关闭，避免 SaveNow 被提前拦截
        SaveNow();
        _closed = true;
        _saveTimer.Stop();
        _autoScrollTimer?.Stop();
        _hideBarsTimer?.Stop();
        _pageLoadCts?.Cancel();
        try { _loader?.Dispose(); } catch { }
        try { _epubZip?.Dispose(); } catch { }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
        App.Settings.Theme = ThemeService.Current;
        ReaderThemeGlyph.Text = ThemeService.Current == "dark" ? "☾" : "☀";
        ApplyThemeToNovelSettings();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    private void ToggleBars()
    {
        _barsVisible = !_barsVisible;
        TopBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!App.Settings.AutoHideBars) return;
        if (!_barsVisible) return;
        (_hideBarsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }).Stop();
        _hideBarsTimer.Tick -= HideBars_Tick;
        _hideBarsTimer.Tick += HideBars_Tick;
        _hideBarsTimer.Start();
    }

    private void HideBars_Tick(object? sender, EventArgs e)
    {
        if (_hideBarsTimer is not null) _hideBarsTimer.Stop();
        if (_mode == "novel") return;
        if (_barsVisible && !IsMouseOver)
            ToggleBars();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_barsVisible && (_mode != "novel" || ChapterPanel.Visibility == Visibility.Visible ||
                                     SettingsPanel.Visibility == Visibility.Visible || BookmarkPanel.Visibility == Visibility.Visible))
                {
                    ChapterPanel.Visibility = SettingsPanel.Visibility = BookmarkPanel.Visibility = Visibility.Collapsed;
                    ToggleBars();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.F:
                Fullscreen_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.B:
                BookmarkButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.M:
                ToggleBars();
                e.Handled = true;
                break;
            case Key.Space:
            case Key.PageDown:
                Next();
                e.Handled = true;
                break;
            case Key.PageUp:
                Prev();
                e.Handled = true;
                break;
            case Key.Left:
                if (_rtl) Next(); else Prev();
                e.Handled = true;
                break;
            case Key.Right:
                if (_rtl) Prev(); else Next();
                e.Handled = true;
                break;
            case Key.Home:
                if (_mode == "webtoon") WebtoonView.ScrollToPage(0);
                else if (_mode == "double") { _spreadStart = 0; RenderDoubleSpread(); }
                else if (_mode == "novel") LoadNovelChapter(0);
                else LoadPageAsync(0);
                e.Handled = true;
                break;
            case Key.End:
                if (_mode == "webtoon") WebtoonView.ScrollToPage(_loader!.PageCount - 1);
                else if (_mode == "double") { _spreadStart = _loader!.PageCount - 1; RenderDoubleSpread(); }
                else if (_mode == "novel") LoadNovelChapter(_novel!.Chapters.Count - 1);
                else LoadPageAsync(_loader!.PageCount - 1);
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                if (_mode != "novel") ZoomIn_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                if (_mode != "novel") ZoomOut_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.D0:
                if (_mode != "novel") FitPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Up:
                if (_mode == "webtoon") WebtoonView.ScrollBy(-80);
                else if (_mode == "novel" && _novelSubMode == "scroll")
                {
                    var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                    if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset - 60);
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (_mode == "webtoon") WebtoonView.ScrollBy(80);
                else if (_mode == "novel" && _novelSubMode == "scroll")
                {
                    var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                    if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset + 60);
                }
                e.Handled = true;
                break;
        }
    }

    private void ShowError(string message)
    {
        ComicPagedView.Visibility = WebtoonView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }

    private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _sliderActive = true;
    }

    private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _sliderActive = false;
        // 拖动进度条过程中只记录位置，松手时才真正跳页，避免中间位置触发大量解码
        ApplySliderJump();
    }

    #endregion
}

public sealed class BookmarkItem
{
    public BookmarkItem(Bookmark bookmark, string display, string timeText)
    {
        Bookmark = bookmark;
        Display = display;
        TimeText = timeText;
    }

    public Bookmark Bookmark { get; }
    public string Display { get; }
    public string TimeText { get; }
}
