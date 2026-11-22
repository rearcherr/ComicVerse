using System.IO;
using System.IO.Compression;
using System.Globalization;
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
    private int _doublePageCount = 1;
    private int _doubleLeftIndex = -1;
    private int _doubleRightIndex = -1;
    private double _doubleAppliedScale = 1.0;
    private bool _rtl;
    private bool _webtoonInitialized;
    private Task? _webtoonInitializationTask;
    private bool _webtoonRestoringPosition;
    private long _webtoonEntryVersion;
    private double _webtoonFraction;
    private double? _webtoonRestoreFraction;

    private int _novelChapter;
    /// <summary>待定位到末页的章节号：回退到上一章时用（-1 表示无）。</summary>
    private int _novelPendingLastPage = -1;
    private int _novelPage;
    private double _novelScrollFraction;
    private bool _restoringNovelScrollPosition;
    private int _novelScrollRestoreVersion;
    private double? _pendingNovelPageFraction;
    private string _novelSubMode = "paged";
    private readonly Dictionary<int, int> _chapterPages = new();
    private DynamicDocumentPaginator? _waitingNovelPaginator;
    private EventHandler? _novelPaginationCompleted;
    private int _novelPaginationVersion;
    private bool _novelPageLayoutPending;
    private NovelViewSettings _novelSettings = new();
    private bool _novelSettingsLoaded;
    private bool _novelSettingsDirty;
    private bool _syncingNovelSpacingInputs;
    private readonly HashSet<TextBox> _novelSpacingDrafts = new();
    private Encoding? _txtEncoding;

    private readonly DispatcherTimer _saveTimer;
    private DispatcherTimer? _rebuildTimer;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _hideBarsTimer;
    private bool _closed;
    private bool _updatingSlider;
    private bool _sliderActive;
    private bool _changingChapter;
    private bool _interactionReady;
    /// <summary>正在用持久化值给排版控件赋初值：此时控件事件不算"用户改动"，不要触发重排/回写。</summary>
    private bool _loadingNovelUi;
    private bool _barsVisible = true;
    private CancellationTokenSource? _pageLoadCts;
    private long _pageLoadVersion;
    private CancellationTokenSource? _doubleLoadCts;
    private long _doubleLoadVersion;

    public ReaderWindow(Book book, bool fromStart = false)
    {
        _book = book;
        _fromStart = fromStart;
        InitializeComponent();
        InstallNovelManualInputHandlers();
        InstallComicPageJumpCommand();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => SaveNow();

        _rtl = App.Settings.MangaRightToLeft;
        UpdateRtlGlyph();
        ReaderThemeGlyph.Text = ThemeService.Current == "dark" ? "\uE708" : "\uE706";
        SetInteractionReady(false);
    }

    // 自检钩子（仅在 --smoke 模式下使用）
    internal bool IsComicPageLoaded => _mode == "paged" && _pagedPresentedPage == _page
        && PageImage.Source is not null && LoadingPanel.Visibility == Visibility.Collapsed;
    internal bool IsWebtoonReady => _webtoonInitialized && WebtoonView.IsReady && !_webtoonRestoringPosition && WebtoonView.PageCount > 0;
    internal int WebtoonPageCount => WebtoonView.PageCount;
    /// <summary>小说是否已就绪：翻页模式看 NovelPaged，滚动模式看 NovelScroll（二者互斥占用同一个文档）。</summary>
    internal bool IsNovelReady => _novel is not null && (NovelPaged.Document is not null || NovelScroll.Document is not null);
    internal bool IsDoubleReady => _mode == "double" && !_doubleNavigationRunning
        && _comicPresentation is { Mode: "double" } presented && presented.Page == _spreadStart
        && DoubleLoading.Visibility == Visibility.Collapsed && LeftImage.Source is not null
        && (presented.VisiblePages == 1 || RightImage.Source is not null);
    internal string WebtoonStats => $"canvas={WebtoonView.CanvasWidth:F0}x{WebtoonView.CanvasHeight:F0} rendered={WebtoonView.RenderedCount} scale={_zoom:F2}";
    internal void TestSwitchToWebtoon() => ModeWebtoon.IsChecked = true;
    internal void TestSwitchToDouble() => ModeDouble.IsChecked = true;
    internal void TestSwitchToPaged() => ModePaged.IsChecked = true;
    internal void TestNext() => Next();
    internal void TestWebtoonScrollBy(double dy) => WebtoonView.ScrollBy(dy);
    internal void TestJumpToPage(int page) => LoadPageAsync(page);
    internal void TestWebtoonJumpTo(int page) => NavigateWebtoonToPage(page);
    internal void TestWebtoonDragDown(double dy) => WebtoonView.TestDragDown(dy);
    internal (bool Ok, string Detail) TestWebtoonClickRelease() => WebtoonView.TestRoutedClickRelease();
    internal void TestSetRtl(bool rtl) => _rtl = rtl;
    internal int WebtoonCurrentPage => WebtoonView.CurrentPage;

    /// <summary>注入真实的左右方向键事件，走与用户按键完全相同的处理链路。</summary>
    internal void TestPressArrowKey(bool isLeftKey)
    {
        var source = PresentationSource.FromVisual(this);
        if (source is null)
        {
            NavigateHorizontal(isLeftKey);
            return;
        }
        RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount,
            isLeftKey ? Key.Left : Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    internal int WebtoonRenderedCount => WebtoonView.RenderedCount;
    internal int WebtoonRenderedLoadedCount => WebtoonView.RenderedWithSourceCount;
    internal int WebtoonBlankStripCount => WebtoonView.RenderedWithoutSourceCount;
    internal double WebtoonScrollOffset => WebtoonView.ScrollOffset;
    internal double WebtoonHorizontalScrollableWidth => WebtoonView.HorizontalScrollableWidth;
    internal double WebtoonHorizontalOffset => WebtoonView.HorizontalOffset;
    internal (int First, int Last) WebtoonVisibleRange => (WebtoonView.VisibleFirst, WebtoonView.VisibleLast);
    internal int CurrentPageNumber => _page;
    internal int ComicPageCount => _loader?.PageCount ?? 0;
    /// <summary>双页模式下的当前跨页起始页（相当于"当前游标"）。</summary>
    internal int CurrentSpreadStart => _spreadStart;
    internal int DoubleLeftPageIndex => _doubleLeftIndex;
    internal int DoubleRightPageIndex => _doubleRightIndex;
    internal double DoubleAppliedScale => _doubleAppliedScale;
    internal void TestZoomIn() => ZoomIn_Click(this, new RoutedEventArgs());
    internal void TestRefreshDouble() => RenderDoubleSpread(_comicPendingNavigation);
    internal void TestScrollWebtoonHorizontal(double offset) => WebtoonView.TestScrollHorizontal(offset);
    internal void TestSetFontSize(double size) => FontSizeSlider.Value = size;
    internal void TestFlushSave() => SaveNow();
    /// <summary>模拟真实拖动进度条：按下 → 连续改值 → 松手跳页。</summary>
    internal void TestSliderDrag(double fromFraction, double toFraction, int steps = 12)
    {
        _sliderActive = true;
        for (int i = 1; i <= steps; i++)
        {
            double f = fromFraction + (toFraction - fromFraction) * i / steps;
            ProgressSlider.Value = Math.Clamp(f, 0, 1) * 1000;
        }
        _sliderActive = false;
        ApplySliderJump();
    }
    internal void TestSetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.2, 3.0);
        _fitMode = "custom";
        ZoomSlider.Value = _zoom * 100;
    }
    internal double CurrentZoom => _mode == "webtoon" ? _zoom
        : _mode == "double" ? _doubleAppliedScale
        : PageScale.ScaleX;
    internal string ZoomTextValue => ZoomText.Text;
    private bool _syncingZoom;
    internal int NovelPage => _novelPage;
    internal int NovelDisplayedPageNumber => NovelPaged.MasterPageNumber;
    internal int NovelDisplayedPageCount => NovelPaged.PageCount;
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

    // ---- 自检钩子：小说子模式 / 滚动位置 / 排版设置（--smoke 使用）----
    internal string NovelSubMode => _novelSubMode;
    internal double NovelScrollFraction => _novelScrollFraction;
    internal bool IsNovelScrollRestoring => _restoringNovelScrollPosition;
    internal bool IsNovelChapterLayoutStable => HasStableNovelChapterLayout();
    internal bool TestNovelScrollViewportToFraction(double fraction)
    {
        var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (sv is null || sv.ScrollableHeight <= 0.5) return false;
        // 仅移动真实视口，不直接更新记录值或调用保存，确保冒烟覆盖 ScrollChanged 路径。
        sv.ScrollToVerticalOffset(Math.Clamp(fraction, 0, 1) * sv.ScrollableHeight);
        return true;
    }
    /// <summary>滚动子模式下滑动视图的实际可滚动高度（0 = 本章一屏就能显示完，滚不动）。</summary>
    internal double NovelScrollableHeight
        => VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll)?.ScrollableHeight ?? 0;
    /// <summary>滚动子模式下 ScrollViewer 的真实滚动位置（0..1）。用于核对"已滚动"是否真的落到了视图上。</summary>
    internal double NovelActualScrollFraction
    {
        get
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            return sv is null || sv.ScrollableHeight <= 0.5 ? 0 : sv.VerticalOffset / sv.ScrollableHeight;
        }
    }
    /// <summary>诊断：当前正在显示的滚动文档有几个块、总字符数。</summary>
    internal string NovelScrollDocDiagnostics
    {
        get
        {
            var doc = NovelScroll.Document as FlowDocument;
            if (doc is null) return "doc=null";
            var blocks = doc.Blocks.ToList();
            int chars = 0;
            foreach (var b in blocks)
                if (b is Paragraph p) chars += new System.Windows.Documents.TextRange(p.ContentStart, p.ContentEnd).Text.Length;
            return $"docBlocks={blocks.Count} docChars={chars} pagedVis={NovelPaged.Visibility} scrollVis={NovelScroll.Visibility} " +
                   $"mode={_mode} sub={_novelSubMode} chIdx={_novelChapter} novelCh={_novel?.Chapters.Count} settingsFS={_novelSettings.FontSize}";
        }
    }
    /// <summary>诊断：小说是否已初始化完成。</summary>
    internal string NovelProbeDetail
        => $"novelParsed={_novel?.Chapters.Count ?? -1} viewVis={NovelView.Visibility} pagedDoc={(NovelPaged.Document is null ? "null" : "set")} " +
           $"scrollDoc={(NovelScroll.Document is null ? "null" : "set")} title={TitleText.Text}";
    /// <summary>诊断：解析出的小说章节数。</summary>
    internal int NovelChapterCount => _novel?.Chapters.Count ?? 0;
    /// <summary>诊断：本次打开过程中章节被改写的轨迹（临时探针）。</summary>
    internal static readonly List<string> longNovelTrack = new();
    internal IReadOnlyList<string> NovelTrace => longNovelTrack;
    /// <summary>把滚动视图滚到本章的指定相对位置，并同步记录到 _novelScrollFraction。</summary>
    internal bool TestNovelScrollToFraction(double fraction)
    {
        if (_novelSubMode != "scroll")
        {
            NovelModeScroll.IsChecked = true;
            ApplyNovelSubMode();
            // 刚切到滚动模式时 FlowDocument 还没挂上、ScrollableHeight 仍为 0，
            // 这里不能在同一个同步回合里就判定失败：先让布局跑完再试。
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        }
        var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (sv is null || sv.ScrollableHeight <= 0.5) return false;
        _novelScrollFraction = Math.Clamp(fraction, 0, 1);
        sv.ScrollToVerticalOffset(_novelScrollFraction * sv.ScrollableHeight);
        UpdateNovelInfo();
        return true;
    }
    internal double FontSizeSetting => _novelSettings.FontSize;
    internal NovelViewSettings NovelFormattingSnapshot => _novelSettings.Clone();
    internal double NovelDocumentMargin => ((NovelPaged.Document ?? NovelScroll.Document) as FlowDocument)?.PagePadding.Left ?? double.NaN;
    internal double NovelDocumentFontSize => ((NovelPaged.Document ?? NovelScroll.Document) as FlowDocument)?.FontSize ?? double.NaN;
    internal string? NovelDocumentFontFamily => ((NovelPaged.Document ?? NovelScroll.Document) as FlowDocument)?.FontFamily.Source;
    internal IReadOnlyList<string> NovelFontChoices => (FontCombo.ItemsSource as IEnumerable<string>)?.ToArray() ?? [];
    internal void TestSetNovelFormatting(NovelViewSettings settings)
    {
        FontCombo.SelectedItem = (FontCombo.ItemsSource as IEnumerable<string>)?.FirstOrDefault(
            f => string.Equals(f, settings.FontFamily, StringComparison.OrdinalIgnoreCase))
            ?? FontCombo.SelectedItem;
        FontSizeSlider.Value = settings.FontSize;
        LineSpacingSlider.Value = settings.LineSpacing;
        ParagraphSpacingSlider.Value = settings.ParagraphSpacing;
        MarginSlider.Value = settings.PageMargin;
        SelectComboByTag(TextColorCombo, settings.TextColor);
        SelectComboByTag(BgColorCombo, settings.Background);
    }
    internal void TestSetNovelMargin(double margin) => MarginSlider.Value = margin;
    internal string NovelParagraphSpacingText => ParagraphSpacingInput.Text;
    internal string NovelMarginText => MarginInput.Text;
    internal string NovelParagraphSpacingMessage => ParagraphSpacingInputMessage.Text;
    internal string NovelMarginMessage => MarginInputMessage.Text;
    internal void TestSetNovelSpacingText(bool paragraph, string text, bool commit = true)
    {
        var input = paragraph ? ParagraphSpacingInput : MarginInput;
        input.Text = text;
        if (commit) CommitNovelSpacingInput(input, revertInvalid: false);
    }

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
            if (_closed) return;
            SetInteractionReady(true);
            if (_fromStart) SaveNow();
            ContentGrid.Focus();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            Log.Error("阅读器打开失败: " + _book.FilePath, ex);
            ShowError("打开失败：" + ex.Message);
        }
    }

    private async Task InitComicAsync()
    {
        // 打开压缩包/PDF 可能耗时（枚举归档、逐页取尺寸），放到后台线程避免界面卡住
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "正在打开…";
        string path = _book.FilePath;
        var source = await Task.Run(() => ComicSourceFactory.Create(path));
        if (_closed)
        {
            source.Dispose();
            return;
        }
        _source = source;
        int prefetch = App.Settings.LowPerformanceMode ? 1 : App.Settings.PrefetchPages;
        _loader = new ComicImageLoader(_source, App.SharedCache, prefetch);

        ModePaged.Visibility = ModeWebtoon.Visibility = ModeDouble.Visibility = Visibility.Visible;
        RtlButton.Visibility = Visibility.Visible;
        ZoomOutButton.Visibility = ZoomInButton.Visibility = ZoomSlider.Visibility =
            ZoomText.Visibility = FitWidthButton.Visibility = FitPageButton.Visibility = ActualButton.Visibility = Visibility.Visible;

        var prog = _fromStart ? null : App.Library.GetProgress(_book.Id);
        _page = prog is null ? 0 : Math.Clamp(prog.PageIndex, 0, _loader.PageCount - 1);
        _comicInitialPage = _page;
        UpdatePresentedComicInfo();
        _webtoonFraction = prog?.ScrollOffset ?? 0;
        _webtoonRestoreFraction = prog?.Mode == "webtoon" ? _webtoonFraction : null;
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
    }

    private async Task InitNovelAsync()
    {
        ModePaged.Visibility = ModeWebtoon.Visibility = ModeDouble.Visibility = Visibility.Collapsed;
        RtlButton.Visibility = Visibility.Collapsed;
        ZoomOutButton.Visibility = ZoomInButton.Visibility = ZoomSlider.Visibility =
            ZoomText.Visibility = FitWidthButton.Visibility = FitPageButton.Visibility = ActualButton.Visibility = Visibility.Collapsed;
        ChaptersButton.Visibility = SettingsPanelButton.Visibility = Visibility.Visible;
        NovelSearchButton.Visibility = Visibility.Visible;

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
            if (_closed) return;
            var enc = _txtEncoding ?? EncodingDetector.Detect(bytes);
            string text = enc.GetString(bytes);
            _novel = await Task.Run(() => TxtParser.Parse(text, _book.Title));
        }
        if (_closed) return;

        if (_novel!.Chapters.Count == 0)
        {
            ShowError("小说内容为空或无法解析");
            return;
        }

        _novelSettings = App.Settings.GetNovelSettingsForBook(_book.Id);
        // 持久化的排版设置没有任何范围校验：JSON 里出现 null/"" 的颜色会让 NovelDocumentBuilder
        // 抛 NullReferenceException/FormatException（整本小说打不开），越界的字号也会炸。
        // NovelDocumentBuilder 属于 Core（别人在改），所以在这里做兜底校验。
        SanitizeNovelSettings(_novelSettings);
        ChapterList.ItemsSource = _novel.Chapters.Select((c, i) => $"{i + 1}. {c.Title}").ToList();
        LoadNovelSettingsUi();
        ApplyThemeToNovelSettings(rebuild: false);
        _novelSettingsLoaded = true;

        var prog = _fromStart ? null : App.Library.GetProgress(_book.Id);
        int chapter = prog is null ? 0 : Math.Clamp(prog.ChapterIndex, 0, _novel.Chapters.Count - 1);
        _novelPage = prog?.PageIndex ?? 0;
        _novelScrollFraction = prog?.ScrollOffset ?? 0;
        // 子模式此前恒为「翻页」，导致滚动模式的阅读位置每次都被丢弃。
        // 现在把子模式编进 Mode（"novel:scroll" / "novel:paged"）并读回。
        _novelSubMode = _fromStart ? "paged" : ParseNovelSubMode(prog?.Mode);
        if (_novelSubMode == "scroll") NovelModeScroll.IsChecked = true;
        else NovelModePaged.IsChecked = true;
        NovelView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = WebtoonView.Visibility = DoubleView.Visibility = Visibility.Collapsed;
        _mode = "novel";
        ApplyNovelSubMode();
        LoadNovelChapter(chapter, resetPosition: false);
    }

    #region 漫画：翻页

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var rb = (RadioButton)sender;
        if (rb.IsChecked != true) return;
        CloseComicPageJump();
        switch ((string)rb.Tag)
        {
            case "webtoon": ShowWebtoon(); break;
            case "double": ShowDouble(); break;
            default: ShowComicPaged(_page); break;
        }
    }

    private void ShowComicPaged(int index)
    {
        string previousMode = _mode;
        var intent = GetComicModeEntryIntent();
        int entryPage = intent.Page;
        CancelDoubleNavigation();
        _webtoonEntryVersion++;
        _doubleLoadCts?.Cancel();
        _page = index = entryPage;
        if (previousMode != "paged")
        {
            // A failed target in another mode is not a reading position. Do
            // not expose an unrelated old single-page image during the switch.
            if (!HasRetainedComicPresentation("paged"))
            {
                PageImage.Source = null;
                _pagedPresentedPage = -1;
            }
        }
        _mode = "paged";
        _webtoonRestoringPosition = false;
        ComicPagedView.Visibility = Visibility.Visible;
        WebtoonView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        UpdateZoomControlsForMode();
        SetInteractionReady(_interactionReady);
        LoadPageAsync(index, intent);
    }

    private async void LoadPageAsync(int index, ComicNavigationIntent? transferredIntent = null)
    {
        if (_loader is null || _closed) return;
        index = Math.Clamp(index, 0, _loader.PageCount - 1);
        var intent = transferredIntent ?? BeginComicNavigation(index);

        // 快速跳页时取消上一次未完成的加载，避免解码任务堆积拖慢目标页
        _pageLoadCts?.Cancel();
        var cts = _pageLoadCts = new CancellationTokenSource();
        long version = ++_pageLoadVersion;
        _page = index;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = $"正在加载{ComicPageRange(index, 1)}…";
        UpdatePresentedComicInfo();

        try
        {
            var bmp = await _loader.GetPageAsync(index, cts.Token);
            if (_closed || _mode != "paged" || index != _page || version != _pageLoadVersion
                || !IsCurrentComicNavigation(intent)) return;
            if (bmp is null)
            {
                // 解码失败：保留旧页，给出可重试提示，而不是静默白屏
                LoadingText.Text = ComicLoadFailureText("paged", index, 1);
                FinishComicNavigation(intent);
                return;
            }

            PageImage.Source = bmp;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ApplyFit();
            CommitComicPresentation("paged", index, 1);
            FinishComicNavigation(intent);
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
            if (_closed || _mode != "paged" || index != _page || version != _pageLoadVersion
                || !IsCurrentComicNavigation(intent)) return;
            LoadingText.Text = ComicLoadFailureText("paged", index, 1);
            FinishComicNavigation(intent);
        }
    }

    private void LoadingPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loader is not null && !_closed && _mode == "paged")
        {
            LoadPageAsync(_page);
            e.Handled = true;
        }
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

    private void PageScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactionReady || IsFromScrollBar(e.OriginalSource as DependencyObject)) return;
        double width = Math.Max(1, PageScroll.ViewportWidth);
        double relativeX = e.GetPosition(PageScroll).X / width;
        if (relativeX < 0.35)
        {
            if (_rtl) Next(); else Prev();
        }
        else if (relativeX > 0.65)
        {
            if (_rtl) Prev(); else Next();
        }
        else
        {
            ToggleBars();
        }
        e.Handled = true;
    }

    private static bool IsFromScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ScrollBar) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Prev();

    private void Next_Click(object sender, RoutedEventArgs e) => Next();

    private void Prev()
    {
        if (!_interactionReady) return;
        if (_mode == "novel")
        {
            NovelPrev();
            return;
        }
        switch (_mode)
        {
            case "webtoon":
                if (!WebtoonView.IsReady || _webtoonRestoringPosition) return;
                NavigateWebtoonToPage(Math.Max(0, (_comicPendingNavigation?.Page ?? WebtoonView.CurrentPage) - 1));
                break;
            case "double":
                MoveDoubleSpread(-1);
                break;
            default:
                LoadPageAsync(_page - 1);
                break;
        }
    }

    private void Next()
    {
        if (!_interactionReady) return;
        if (_mode == "novel")
        {
            NovelNext();
            return;
        }
        switch (_mode)
        {
            case "webtoon":
                if (!WebtoonView.IsReady || _webtoonRestoringPosition) return;
                NavigateWebtoonToPage(Math.Min(_loader!.PageCount - 1,
                    (_comicPendingNavigation?.Page ?? WebtoonView.CurrentPage) + 1));
                break;
            case "double":
                MoveDoubleSpread(1);
                break;
            default:
                LoadPageAsync(_page + 1);
                break;
        }
    }

    /// <summary>
    /// 左右方向键的翻页方向。
    /// 只有翻页/双页是「左右翻页」的横向阅读，才受日漫 RTL 设置影响（开启后 ← 为下一页）；
    /// 条漫是纵向连续滚动、小说是文字流，都没有左右翻页方向，因此恒为
    /// 「← 上一页/上一章、→ 下一页/下一章」，与 RTL 设置无关。
    /// </summary>
    private void NavigateHorizontal(bool isLeftKey)
    {
        bool rtlApplies = _mode is "paged" or "double";
        bool forward = rtlApplies ? isLeftKey == _rtl : !isLeftKey;
        if (forward) Next();
        else Prev();
    }

    /// <summary>
    /// 键盘焦点是否落在需要用 ←/→ 的控件上（排版滑块、下拉框、章节目录、输入框）。
    /// PreviewKeyDown 在窗口层先于控件执行，不加这个判断的话整窗的 ←/→ 都会被翻页吞掉，
    /// 「排版」面板里就没法用方向键调滑块的焦点/数值，目录也选不中。
    /// </summary>
    private static bool IsKeyboardInputFocused()
        => Keyboard.FocusedElement is Slider or ComboBox or ComboBoxItem or ListBoxItem or ListBox
            or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ButtonBase;

    private void UpdateComicInfo()
    {
        CaptureCurrentComicPresentation();
        CompletePresentedWebtoonNavigation();
        UpdatePresentedComicInfo();
    }

    #endregion

    #region 漫画：条漫

    private async void ShowWebtoon()
    {
        var intent = GetComicModeEntryIntent();
        int entryPage = intent.Page;
        CancelDoubleNavigation();
        long entryVersion = ++_webtoonEntryVersion;
        _doubleLoadCts?.Cancel();
        _page = entryPage;
        int targetPage = entryPage;
        double? targetFraction = intent.Fraction;
        _webtoonRestoreFraction = null;
        _mode = "webtoon";
        _webtoonRestoringPosition = true;
        _pageLoadCts?.Cancel();
        WebtoonView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        UpdateZoomControlsForMode();
        SetInteractionReady(_interactionReady);

        if (!_webtoonInitialized)
        {
            _webtoonInitialized = true;
            WebtoonView.ScaleChanged += OnWebtoonScaleChanged;
            WebtoonView.CurrentPageChanged += idx =>
            {
                if (_mode != "webtoon" || !WebtoonView.IsReady || _webtoonRestoringPosition) return;
                _page = idx;
                // 同时记住在文档里的相对位置：离开条漫再回来、或下次打开时都靠它恢复
                _webtoonFraction = WebtoonView.ScrollFraction;
                UpdateComicInfo();
                ScheduleSave();
            };
            WebtoonView.ReadingPositionChanged += () =>
            {
                if (_closed || _mode != "webtoon" || !WebtoonView.IsReady || _webtoonRestoringPosition) return;
                _page = WebtoonView.CurrentPage;
                _webtoonFraction = WebtoonView.ScrollFraction;
                UpdateComicInfo();
                ScheduleSave();
            };
            double scale = _zoom;
            int start = _page;
            _webtoonInitializationTask = WebtoonView.InitializeAsync(_loader!, i => _loader!.GetPageSize(i), start, scale);
        }
        // Every entry waits for the same first layout. Only the latest entry may
        // restore its snapshot; an earlier async completion cannot jump it back.
        if (_webtoonInitializationTask is not null) await _webtoonInitializationTask;
        if (_closed || _mode != "webtoon" || entryVersion != _webtoonEntryVersion
            || !IsCurrentComicNavigation(intent)) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closed || _mode != "webtoon" || entryVersion != _webtoonEntryVersion || _loader is null
                || !IsCurrentComicNavigation(intent)) return;
            WebtoonView.EnsureLayout();
            if (targetFraction is double fraction) WebtoonView.ScrollToFraction(fraction);
            else WebtoonView.ScrollToPage(targetPage);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (_closed || _mode != "webtoon" || entryVersion != _webtoonEntryVersion
                    || !IsCurrentComicNavigation(intent)) return;
                WebtoonView.UpdateLayout();
                if (_closed || _mode != "webtoon" || entryVersion != _webtoonEntryVersion
                    || !IsCurrentComicNavigation(intent)) return;
                _webtoonRestoringPosition = false;
                _page = Math.Max(0, WebtoonView.CurrentPage);
                _webtoonFraction = WebtoonView.ScrollFraction;
                UpdateComicInfo();
                SetInteractionReady(_interactionReady);
                ScheduleSave();
            });
        });
    }

    #endregion

    #region 漫画：双页

    private void ShowDouble()
    {
        string previousMode = _mode;
        var intent = GetComicModeEntryIntent();
        int entryPage = intent.Page;
        CancelDoubleNavigation();
        _webtoonEntryVersion++;
        _doubleLoadCts?.Cancel();
        if (previousMode != "double" && !HasRetainedComicPresentation("double"))
        {
            LeftImage.Source = RightImage.Source = null;
            _doubleLeftIndex = _doubleRightIndex = -1;
        }
        _mode = "double";
        _webtoonRestoringPosition = false;
        _pageLoadCts?.Cancel();
        DoubleView.Visibility = Visibility.Visible;
        ComicPagedView.Visibility = WebtoonView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        _page = _spreadStart = Math.Clamp(entryPage, 0, _loader!.PageCount - 1);
        UpdateZoomControlsForMode();
        SetInteractionReady(_interactionReady);
        RenderDoubleSpread(intent);
    }

    private async void RenderDoubleSpread(ComicNavigationIntent? transferredIntent = null)
    {
        if (_loader is null || _closed) return;
        _doubleLoadCts?.Cancel();
        var cts = _doubleLoadCts = new CancellationTokenSource();
        long version = ++_doubleLoadVersion;
        int count = _loader.PageCount;
        _spreadStart = Math.Clamp(_spreadStart, 0, count - 1);
        var intent = transferredIntent ?? BeginComicNavigation(_spreadStart);
        int requestedStart = _spreadStart;
        int requestedLength = 1;
        DoubleLoading.Visibility = Visibility.Visible;
        DoubleLoadingText.Text = $"正在加载{ComicPageRange(requestedStart, 1)}…";
        UpdatePresentedComicInfo();

        bool IsCurrent() => !_closed && _mode == "double" && version == _doubleLoadVersion
                            && !cts.IsCancellationRequested && IsCurrentComicNavigation(intent);
        try
        {
            int spread = await GetCanonicalDoubleSpreadAsync(requestedStart, cts.Token);
            if (!IsCurrent()) return;
            _spreadStart = spread;
            int spreadLength = await GetDoubleSpreadLengthAsync(spread, cts.Token);
            if (!IsCurrent()) return;
            requestedStart = spread;
            requestedLength = spreadLength;
            DoubleLoadingText.Text = $"正在加载{ComicPageRange(spread, spreadLength)}…";
            int second = spreadLength < 2 ? -1 : spread + 1;
            // 日漫跨页从右向左展开：先读到的页在右侧，后一页在左侧。
            int leftIndex = _rtl && second >= 0 ? second : spread;
            int rightIndex = _rtl && second >= 0 ? spread : second;
            var leftBmp = await _loader.GetPageAsync(leftIndex, cts.Token);
            if (!IsCurrent()) return;
            System.Windows.Media.Imaging.BitmapSource? rightBmp = null;
            if (rightIndex >= 0)
            {
                rightBmp = await _loader.GetPageAsync(rightIndex, cts.Token);
                if (!IsCurrent()) return;
            }

            if (leftBmp is null || (rightIndex >= 0 && rightBmp is null))
            {
                DoubleLoadingText.Text = ComicLoadFailureText("double", spread, spreadLength);
                FinishComicNavigation(intent);
                return;
            }
            // Commit the complete spread together. Half a new pair must not
            // replace the previous successful pair or become saved progress.
            LeftImage.Source = leftBmp;
            RightImage.Source = rightBmp;
            _doubleLeftIndex = leftIndex;
            _doubleRightIndex = rightIndex;
            _doublePageCount = spreadLength;
            RightScroll.Visibility = rightIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyDoubleFit();
            CommitComicPresentation("double", spread, spreadLength);
            FinishComicNavigation(intent);
            DoubleLoading.Visibility = Visibility.Collapsed;
            _loader.Prefetch(spread);
            UpdateComicInfo();
            ScheduleSave();
        }
        catch (OperationCanceledException)
        {
            // 更快的翻页、模式切换或关窗已经取代本次渲染
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                Log.Error("加载双页失败", ex);
                DoubleLoadingText.Text = ComicLoadFailureText("double", requestedStart, requestedLength);
                FinishComicNavigation(intent);
            }
        }
        finally
        {
            if (ReferenceEquals(_doubleLoadCts, cts))
                _doubleLoadCts = null;
            cts.Dispose();
        }
    }

    private void DoubleLoading_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loader is null || _closed || _mode != "double") return;
        CancelDoubleNavigation();
        RenderDoubleSpread();
        e.Handled = true;
    }

    private void ApplyDoubleFit()
    {
        double leftScale = ApplyDoubleFit(LeftImage);
        double rightScale = ApplyDoubleFit(RightImage);
        _doubleAppliedScale = leftScale > 0 ? leftScale : rightScale > 0 ? rightScale : _zoom;
        UpdateZoomText(_doubleAppliedScale);
    }

    private double ApplyDoubleFit(Image image)
    {
        if (image.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return 0;
        double viewportWidth = image.Parent is ScrollViewer sv ? Math.Max(50, sv.ViewportWidth) : 800;
        double viewportHeight = image.Parent is ScrollViewer sh ? Math.Max(50, sh.ViewportHeight) : 700;
        double widthScale = viewportWidth / Math.Max(1, bmp.PixelWidth);
        double scale = _fitMode switch
        {
            "page" => Math.Min(widthScale, viewportHeight / Math.Max(1, bmp.PixelHeight)),
            "actual" => 1.0,
            "custom" => _zoom,
            _ => widthScale
        };
        scale = Math.Clamp(scale, 0.05, 8.0);
        image.Width = bmp.PixelWidth * scale;
        image.Height = bmp.PixelHeight * scale;
        return scale;
    }

    private void DoubleView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && _mode == "double" && (LeftImage.Source is not null || RightImage.Source is not null))
            ApplyDoubleFit();
    }

    #endregion

    #region 缩放

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "width";
        if (_mode == "webtoon") _zoom = 1.0;
        ApplyComicFit();
    }

    private void FitPage_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "page";
        ApplyComicFit();
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = "actual";
        ApplyComicFit();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        double basis = _fitMode == "custom" ? _zoom : CurrentVisibleScale;
        _zoom = Math.Clamp(basis * 1.25, 0.2, 3.0);
        _fitMode = "custom";
        SyncZoomSlider();
        ApplyComicFit();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        double basis = _fitMode == "custom" ? _zoom : CurrentVisibleScale;
        _zoom = Math.Clamp(basis / 1.25, 0.2, 3.0);
        _fitMode = "custom";
        SyncZoomSlider();
        ApplyComicFit();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoom) return;
        if (!IsLoaded) return;
        _zoom = Math.Clamp(e.NewValue / 100.0, 0.2, 3.0);
        _fitMode = "custom";
        ApplyComicFit();
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

    private double CurrentVisibleScale
    {
        get
        {
            if (_mode == "webtoon") return _zoom;
            if (_mode == "double") return _doubleAppliedScale;
            return PageImage.Source is null ? _zoom : PageScale.ScaleX;
        }
    }

    private void SyncZoomSlider()
    {
        _syncingZoom = true;
        try
        {
            ZoomSlider.Value = _zoom * 100;
        }
        finally
        {
            _syncingZoom = false;
        }
    }

    private void ApplyComicFit()
    {
        if (_mode == "webtoon")
        {
            WebtoonView.ScaleFactor = _zoom;
            UpdateZoomText(_zoom);
        }
        else if (_mode == "double")
        {
            ApplyDoubleFit();
        }
        else
        {
            ApplyFit();
        }
    }

    private void UpdateZoomControlsForMode()
    {
        bool comic = _mode is "paged" or "double" or "webtoon";
        if (!comic) return;
        FitWidthButton.Visibility = Visibility.Visible;
        FitPageButton.Visibility = _mode == "webtoon" ? Visibility.Collapsed : Visibility.Visible;
        ActualButton.Visibility = _mode == "webtoon" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Rtl_Click(object sender, RoutedEventArgs e)
    {
        _rtl = !_rtl;
        App.Settings.MangaRightToLeft = _rtl;
        UpdateRtlGlyph();
        if (_mode == "double")
            RenderDoubleSpread(_comicPendingNavigation);
    }

    private void UpdateRtlGlyph()
    {
        RtlGlyph.Opacity = _rtl ? 1.0 : 0.35;
        RtlGlyph.Text = _rtl ? "RTL" : "LTR";
    }

    #endregion

    #region 小说

    private void LoadNovelChapter(int index, bool resetPosition = true)
    {
        if (_novel is null || _novel.Chapters.Count == 0) return;
        longNovelTrack.Add($"LoadNovelChapter({index}) 调用方={new System.Diagnostics.StackTrace().GetFrame(1)?.GetMethod()?.Name} reset={resetPosition}");
        _novelChapter = Math.Clamp(index, 0, _novel.Chapters.Count - 1);
        // 定位请求只属于本次加载；旧文档被取消后也不能残留到后来再次打开的同一章。
        bool seekLastPage = !resetPosition && _novelPendingLastPage == _novelChapter;
        double? requestedPageFraction = resetPosition ? null : _pendingNovelPageFraction;
        _novelPendingLastPage = -1;
        _pendingNovelPageFraction = null;
        var chapter = _novel.Chapters[_novelChapter];
        var builder = new NovelDocumentBuilder(_imageResolver);
        var doc = builder.Build(chapter, _novelSettings);
        BeginNovelScrollRestore();
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
            _pendingNovelPageFraction = null;
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
            if (_closed || !ReferenceEquals(doc, NovelPaged.Document ?? NovelScroll.Document)) return;
            if (_novelSubMode == "scroll")
            {
                RestoreNovelScrollFraction();
                UpdateNovelInfo();
                return;
            }
            WhenNovelPaginationReady(pages =>
            {
                if (seekLastPage)
                {
                    _novelPage = pages - 1;
                }
                else if (requestedPageFraction is double pageFraction)
                {
                    _novelPage = (int)Math.Round(Math.Clamp(pageFraction, 0, 1) * (pages - 1));
                }
                _novelPage = Math.Clamp(_novelPage, 0, pages - 1);
                GoToNovelPage();
                UpdateNovelInfo();
            });
        });
    }

    private int ComputeChapterPages()
    {
        if (_novelSubMode == "paged" && NovelPaged.Document is IDocumentPaginatorSource source)
        {
            int pages = Math.Max(1, source.DocumentPaginator.PageCount);
            if (source.DocumentPaginator.IsPageCountValid && IsNovelPagePaddingCurrent())
            {
                _chapterPages[_novelChapter] = pages;
                return pages;
            }
            return _chapterPages.GetValueOrDefault(_novelChapter, pages);
        }
        return _chapterPages.GetValueOrDefault(_novelChapter, 1);
    }

    private void CancelNovelPaginationWait()
    {
        _novelPaginationVersion++;
        if (_waitingNovelPaginator is not null && _novelPaginationCompleted is not null)
            _waitingNovelPaginator.PaginationCompleted -= _novelPaginationCompleted;
        _waitingNovelPaginator = null;
        _novelPaginationCompleted = null;
        _novelPageLayoutPending = false;
    }

    private void WhenNovelPaginationReady(Action<int> ready)
    {
        CancelNovelPaginationWait();
        if (_closed || _novelSubMode != "paged" || NovelPaged.Document is not IDocumentPaginatorSource source) return;
        var document = NovelPaged.Document;
        var paginator = source.DocumentPaginator;
        EnsureNovelPagePadding();
        int version = _novelPaginationVersion;
        _novelPageLayoutPending = true;
        void Apply()
        {
            if (_closed || version != _novelPaginationVersion || _novelSubMode != "paged"
                || !ReferenceEquals(document, NovelPaged.Document)) return;
            EnsureNovelPagePadding();
            if (!paginator.IsPageCountValid)
            {
                WhenNovelPaginationReady(ready);
                return;
            }
            int pages = Math.Max(1, paginator.PageCount);
            _chapterPages[_novelChapter] = pages;
            _novelPageLayoutPending = false;
            ready(pages);
        }
        if (paginator.IsPageCountValid)
        {
            Apply();
            return;
        }
        if (paginator is DynamicDocumentPaginator dynamicPaginator)
        {
            _waitingNovelPaginator = dynamicPaginator;
            _novelPaginationCompleted = (_, _) =>
            {
                if (version != _novelPaginationVersion) return;
                dynamicPaginator.PaginationCompleted -= _novelPaginationCompleted;
                _waitingNovelPaginator = null;
                _novelPaginationCompleted = null;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Apply);
            };
            dynamicPaginator.PaginationCompleted += _novelPaginationCompleted;
            dynamicPaginator.IsBackgroundPaginationEnabled = true;
        }
    }

    private void GoToNovelPage()
    {
        if (_closed || _novelSubMode != "paged" || NovelPaged.Document is not FlowDocument document) return;
        RegisterNovelExplicitPageNavigation(document, _novelPage);
        // 持久化页索引从 0 开始，WPF 的 GoToPage 则从 1 开始。
        // 直接传索引会让第一次翻页仍停在首页，并在接章时跳过当前章的最后一页。
        NovelPaged.GoToPage(_novelPage + 1);
        SettleNovelExplicitPageNavigation();
    }

    /// <summary>阅读进度里的 Mode 对小说是 "novel:paged" / "novel:scroll"；旧库可能是 "novel" 或空串。</summary>
    private static string ParseNovelSubMode(string? mode)
        => mode is not null && mode.EndsWith(":scroll", StringComparison.Ordinal) ? "scroll" : "paged";

    private void ApplyNovelSubMode()
    {
        var doc = (NovelPaged.Document ?? NovelScroll.Document) as FlowDocument;
        double? requestedPageFraction = _pendingNovelPageFraction;
        _pendingNovelPageFraction = null;
        BeginNovelScrollRestore();
        NovelPaged.Document = null;
        NovelScroll.Document = null;
        if (_novelSubMode == "scroll")
        {
            if (doc is not null) doc.PagePadding = new Thickness(_novelSettings.PageMargin);
            NovelScroll.Visibility = Visibility.Visible;
            NovelPaged.Visibility = Visibility.Collapsed;
            if (doc is not null) NovelScroll.Document = doc;
            if (_novel is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (_closed) return;
                    // doc 为 null 也要重试：InitNovelAsync 里本方法先于 LoadNovelChapter 执行，
                    // 那时文档还没挂上；但此刻 _novel 与 _novelScrollFraction 都已就绪。
                    RestoreNovelScrollFraction();
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
                if (_closed || !ReferenceEquals(doc, NovelPaged.Document)) return;
                WhenNovelPaginationReady(pages =>
                {
                    _novelPage = requestedPageFraction is double fraction
                        ? (int)Math.Round(Math.Clamp(fraction, 0, 1) * (pages - 1))
                        : Math.Clamp(_novelPage, 0, pages - 1);
                    GoToNovelPage();
                    UpdateNovelInfo();
                });
            });
        }
        StopAutoScroll();
    }

    /// <summary>
    /// 把 <see cref="_novelScrollFraction"/> 落到滚动视图上。打开流程里 ApplyNovelSubMode 先于
    /// LoadNovelChapter 执行（此时文档还没挂上），所以这里要一直重试到真正有滚动高度为止，
    /// 否则"上次读到一半"会被悄悄恢复成章首。
    /// </summary>
    private void BeginNovelScrollRestore()
    {
        CancelNovelPageNavigation();
        ResetNovelWheelTarget();
        CancelNovelPaginationWait();
        _novelScrollRestoreVersion++;
        _restoringNovelScrollPosition = _novelSubMode == "scroll";
    }

    private void RestoreNovelScrollFraction(int attempt = 0, int restoreVersion = -1)
    {
        if (_closed || _novel is null || _novelSubMode != "scroll") return;
        if (restoreVersion < 0) restoreVersion = _novelScrollRestoreVersion;
        if (restoreVersion != _novelScrollRestoreVersion) return;
        var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (sv is not null && sv.ScrollableHeight <= 0.5)
        {
            NovelScroll.UpdateLayout();
            sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll) ?? sv;
        }
        if (sv is not null && (sv.ScrollableHeight > 0.5 || _novelScrollFraction <= 0 || attempt >= MaxScrollRestoreAttempts))
        {
            sv.ScrollToVerticalOffset(_novelScrollFraction * sv.ScrollableHeight);
            // ScrollToVerticalOffset 的布局是延迟完成的：恢复期间不能把临时 0 偏移存回进度。
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (_closed || _novelSubMode != "scroll" || restoreVersion != _novelScrollRestoreVersion) return;
                _restoringNovelScrollPosition = false;
                UpdateNovelInfo();
            });
            return;
        }
        if (attempt >= MaxScrollRestoreAttempts)
        {
            _restoringNovelScrollPosition = false;
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RestoreNovelScrollFraction(attempt + 1, restoreVersion));
    }

    private const int MaxScrollRestoreAttempts = 30;

    private void NovelScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ObserveNovelWheelScroll(e);
        if (_closed || !_interactionReady || _mode != "novel" || _novelSubMode != "scroll" ||
            _restoringNovelScrollPosition || e.VerticalChange == 0) return;
        UpdateNovelInfo();
    }

    private void NovelMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string nextMode = NovelModeScroll.IsChecked == true ? "scroll" : "paged";
        if (nextMode == _novelSubMode) return;
        // 初始化会程序设置单选框；只有已显示正文后的用户切换才转换当前阅读位置。
        if (_mode == "novel" && _novel is not null && (NovelPaged.Document ?? NovelScroll.Document) is not null)
        {
            double fraction;
            if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                fraction = !_restoringNovelScrollPosition && sv is not null && sv.ScrollableHeight > 0.5
                    ? sv.VerticalOffset / sv.ScrollableHeight : _novelScrollFraction;
            }
            else
            {
                SynchronizeNovelPageFactsNow();
                int pages = ComputeChapterPages();
                fraction = pages > 1 ? (double)_novelPage / (pages - 1) : 0;
            }
            _novelScrollFraction = Math.Clamp(fraction, 0, 1);
            _pendingNovelPageFraction = nextMode == "paged" ? _novelScrollFraction : null;
        }
        _novelSubMode = nextMode;
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
        if (ChapterPanel.Visibility == Visibility.Visible) SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void SettingsPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPanel.Visibility == Visibility.Visible) ChapterPanel.Visibility = Visibility.Collapsed;
    }

    private void NovelPrev()
    {
        if (_novelSubMode == "scroll")
        {
            if (NovelScrollStep(-1)) return;   // 已在章首 → 接上一章
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset - sv.ViewportHeight * 0.9);
            UpdateNovelInfo();
            return;
        }
        SynchronizeNovelPageFactsNow();
        if (!HasValidNovelPagination()) return;
        if (_novelPage > 0)
        {
            _novelPage--;
            GoToNovelPage();
        }
        else if (_novelChapter > 0)
        {
            // 章首再往前 → 回到上一章末尾（与 NovelNext 的章末接下一章对称）
            _novelPendingLastPage = _novelChapter - 1;
            LoadNovelChapter(_novelChapter - 1, resetPosition: false);
        }
        UpdateNovelInfo();
        ScheduleSave();
    }

    private void NovelNext()
    {
        if (_novelSubMode == "scroll")
        {
            if (NovelScrollStep(+1)) return;   // 已在章末 → 接下一章
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            if (sv is not null) sv.ScrollToVerticalOffset(sv.VerticalOffset + sv.ViewportHeight * 0.9);
            UpdateNovelInfo();
            return;
        }
        SynchronizeNovelPageFactsNow();
        if (!HasValidNovelPagination()) return;
        int pages = ComputeChapterPages();
        if (_novelPage < pages - 1 || _novelChapter < _novel!.Chapters.Count - 1)
        {
            if (_novelPage < pages - 1)
            {
                _novelPage++;
                GoToNovelPage();
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
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            // 注意：文档还没量出高度时不能把 _novelScrollFraction 归零——那时 VerticalOffset 恒为 0，
            // 归零会把刚从上一次阅读恢复出来的滚动位置直接抹掉（重开就回到章首）。
            // 只有视图真的可滚动时才用它的实际位置覆盖记录值。
            if (!_restoringNovelScrollPosition && sv is not null && sv.ScrollableHeight > 0.5)
                _novelScrollFraction = Math.Clamp(sv.VerticalOffset / sv.ScrollableHeight, 0, 1);
        }
        double fraction = GetNovelChapterReadFraction(_novelScrollFraction);
        PageInfoText.Text = $"第 {_novelChapter + 1}/{chapters} 章 · 第 {_novelPage + 1} 页";
        SetSlider((_novelChapter + fraction) / chapters);
        ScheduleSave();
    }

    private bool HasStableNovelChapterLayout()
    {
        if (_closed || _novel is null || !NovelView.IsVisible) return false;
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            return !_restoringNovelScrollPosition && NovelScroll.Document is not null && NovelScroll.IsVisible
                && NovelScroll.IsMeasureValid && NovelScroll.IsArrangeValid
                && sv is not null && sv.ViewportHeight > 0 && sv.ExtentHeight > 0;
        }
        return HasValidNovelPagination() && NovelPaged.IsVisible
            && NovelPaged.IsMeasureValid && NovelPaged.IsArrangeValid && NovelPaged.ActualHeight > 0
            && NovelPaged.PageCount > 0;
    }

    private bool HasValidNovelPagination()
        => !_novelPageLayoutPending && NovelPaged.Document is IDocumentPaginatorSource source
            && source.DocumentPaginator.IsPageCountValid && IsNovelPagePaddingCurrent();

    private double GetNovelChapterReadFraction(double scrollFraction)
    {
        if (_novelSubMode == "scroll")
        {
            var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
            // 整章已经完整显示在一屏内即算读完；恢复中的临时零高度不能用于判断。
            if (HasStableNovelChapterLayout() && sv is not null && sv.ScrollableHeight <= 0.5) return 1;
            return Math.Clamp(scrollFraction, 0, 1);
        }
        // ComputeChapterPages 在布局未完成时会回退为 1，必须检查真实分页已完成。
        if (HasStableNovelChapterLayout() && NovelPaged.PageCount == 1) return 1;
        int pages = ComputeChapterPages();
        return pages > 1 ? Math.Clamp((double)_novelPage / (pages - 1), 0, 1) : 0;
    }

    private void LoadNovelSettingsUi()
    {
        // 这里给控件赋初值会触发 SelectionChanged/ValueChanged → NovelSetting_Changed。
        // 没有这个旗标的话，每次打开小说都会在 350ms 后"回读"一遍并覆盖设置（还会把全局默认改掉）。
        _loadingNovelUi = true;
        try
        {
            var fonts = System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(200)
                .ToList();
            // 系统字体很多时已保存字体可能排在前 200 项之外；保留它，避免仅改边距就丢掉字体选择。
            if (!string.IsNullOrWhiteSpace(_novelSettings.FontFamily)
                && !fonts.Any(f => string.Equals(f, _novelSettings.FontFamily, StringComparison.OrdinalIgnoreCase)))
                fonts.Add(_novelSettings.FontFamily);
            FontCombo.ItemsSource = fonts;
            FontCombo.SelectedItem = fonts.FirstOrDefault(f => string.Equals(f, _novelSettings.FontFamily, StringComparison.OrdinalIgnoreCase))
                                     ?? fonts.FirstOrDefault(f => f.Contains(_novelSettings.FontFamily, StringComparison.OrdinalIgnoreCase))
                                     ?? fonts.FirstOrDefault(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase));

            // 越界的持久化值会被 Slider 静默钳到边界，再被回写成"用户选择"，所以先夹紧再赋值
            FontSizeSlider.Value = Math.Clamp(_novelSettings.FontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
            LineSpacingSlider.Value = Math.Clamp(_novelSettings.LineSpacing, LineSpacingSlider.Minimum, LineSpacingSlider.Maximum);
            ParagraphSpacingSlider.Value = Math.Clamp(_novelSettings.ParagraphSpacing, ParagraphSpacingSlider.Minimum, ParagraphSpacingSlider.Maximum);
            MarginSlider.Value = Math.Clamp(_novelSettings.PageMargin, MarginSlider.Minimum, MarginSlider.Maximum);
            SyncNovelSpacingInput(ParagraphSpacingSlider);
            SyncNovelSpacingInput(MarginSlider);
            SelectComboByTag(TextColorCombo, _novelSettings.TextColor);
            SelectComboByTag(BgColorCombo, _novelSettings.Background);
            EncodingCombo.SelectedIndex = 0;
            AutoSpeedSlider.Value = 60;
        }
        finally
        {
            _loadingNovelUi = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // 已保存的合法颜色可能不在预设列表里，保留显示，避免下次改边距时被顺带重置。
        var custom = new ComboBoxItem { Content = "自定义", Tag = tag };
        combo.Items.Add(custom);
        combo.SelectedItem = custom;
    }

    /// <summary>
    /// 把持久化来的排版设置夹到安全范围：颜色为 null/空/无法解析时回落到默认值，
    /// 字号等数值夹到滑块范围内。不这么做的话，一条脏设置会让这本书永久打不开。
    /// </summary>
    private static void SanitizeNovelSettings(NovelViewSettings s)
    {
        s.FontFamily = string.IsNullOrWhiteSpace(s.FontFamily) ? "Microsoft YaHei UI" : s.FontFamily;
        s.TextColor = NormalizeColor(s.TextColor, "#E8E8F4");
        s.Background = NormalizeColor(s.Background, "#1A1A2E");
        s.FontSize = double.IsFinite(s.FontSize) ? Math.Clamp(s.FontSize, 12, 32) : 18;
        s.LineSpacing = double.IsFinite(s.LineSpacing) ? Math.Clamp(s.LineSpacing, 1.2, 2.5) : 1.7;
        s.ParagraphSpacing = double.IsFinite(s.ParagraphSpacing)
            ? Math.Round(Math.Clamp(s.ParagraphSpacing, 0, 120), MidpointRounding.AwayFromZero) : 8;
        s.PageMargin = double.IsFinite(s.PageMargin)
            ? Math.Round(Math.Clamp(s.PageMargin, 0, 240), MidpointRounding.AwayFromZero) : 56;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            _ = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    private void NovelSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingNovelUi || !_novelSettingsLoaded || !IsLoaded || _closed) return;
        _novelSettingsDirty = true;
        (_rebuildTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) }).Stop();
        _rebuildTimer.Tick -= RebuildNovel_Tick;
        _rebuildTimer.Tick += RebuildNovel_Tick;
        _rebuildTimer.Start();
    }

    private void NovelSettingSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SyncNovelSpacingInput(sender as Slider);
        if (_loadingNovelUi || !IsLoaded || _closed) return;
        NovelSetting_Changed(sender, new RoutedEventArgs());
    }

    private void SyncNovelSpacingInput(Slider? slider)
    {
        TextBox? input = ReferenceEquals(slider, ParagraphSpacingSlider) ? ParagraphSpacingInput
            : ReferenceEquals(slider, MarginSlider) ? MarginInput : null;
        TextBlock? message = ReferenceEquals(slider, ParagraphSpacingSlider) ? ParagraphSpacingInputMessage
            : ReferenceEquals(slider, MarginSlider) ? MarginInputMessage : null;
        if (slider is null || input is null) return;
        _syncingNovelSpacingInputs = true;
        try
        {
            input.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture);
            _novelSpacingDrafts.Remove(input);
            if (message is not null)
            {
                message.Text = "";
                message.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _syncingNovelSpacingInputs = false;
        }
    }

    private void NovelSpacingInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingNovelSpacingInputs || _loadingNovelUi || !_novelSettingsLoaded || _closed) return;
        var input = (TextBox)sender;
        _novelSpacingDrafts.Add(input);
        var message = ReferenceEquals(input, ParagraphSpacingInput) ? ParagraphSpacingInputMessage : MarginInputMessage;
        message.Text = "";
        message.Visibility = Visibility.Collapsed;
        // 键入中的空值、负号或部分数字只是草稿；确认后才安排重排和持久化。
    }

    private bool CommitNovelSpacingInput(TextBox input, bool revertInvalid)
    {
        if (!_novelSettingsLoaded || _loadingNovelUi || _closed || !_novelSpacingDrafts.Contains(input)) return true;
        var slider = ReferenceEquals(input, ParagraphSpacingInput) ? ParagraphSpacingSlider : MarginSlider;
        var message = ReferenceEquals(input, ParagraphSpacingInput) ? ParagraphSpacingInputMessage : MarginInputMessage;
        string range = $"{slider.Minimum:0}–{slider.Maximum:0}";
        if (!int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            if (revertInvalid)
                SyncNovelSpacingInput(slider);
            message.Text = revertInvalid ? $"已恢复为 {slider.Value:0}；请输入 {range} 的整数" : $"请输入 {range} 的整数";
            message.Visibility = Visibility.Visible;
            return false;
        }
        double accepted = Math.Clamp(value, slider.Minimum, slider.Maximum);
        _novelSpacingDrafts.Remove(input);
        slider.Value = accepted;
        SyncNovelSpacingInput(slider);
        if (accepted != value)
        {
            message.Text = $"已调整为 {accepted:0}（范围 {range}）";
            message.Visibility = Visibility.Visible;
        }
        return true;
    }

    private void NovelSpacingInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitNovelSpacingInput((TextBox)sender, revertInvalid: false);
        e.Handled = true;
    }

    private void NovelSpacingInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitNovelSpacingInput((TextBox)sender, revertInvalid: true);

    private void CommitNovelSpacingDrafts()
    {
        foreach (var input in _novelSpacingDrafts.ToArray())
            CommitNovelSpacingInput(input, revertInvalid: true);
    }

    private void RebuildNovel_Tick(object? sender, EventArgs e)
    {
        if (_rebuildTimer is not null) _rebuildTimer.Stop();
        // 关窗/初始化失败后 tick 仍可能到期：没有守卫时会在已销毁的控件上重排文档
        if (_closed || !IsLoaded || _novel is null || _novel.Chapters.Count == 0) return;
        FlushNovelSettingsIfDirty();
        RebuildNovelDocumentNow();
    }

    private bool ReadNovelSettingsFromUi()
    {
        if (TextColorCombo.SelectedItem is not ComboBoxItem textColor) return false;
        if (BgColorCombo.SelectedItem is not ComboBoxItem bgColor) return false;
        _novelSettings.FontFamily = FontCombo.SelectedItem as string ?? _novelSettings.FontFamily;
        _novelSettings.FontSize = FontSizeSlider.Value;
        _novelSettings.LineSpacing = LineSpacingSlider.Value;
        _novelSettings.ParagraphSpacing = ParagraphSpacingSlider.Value;
        _novelSettings.PageMargin = MarginSlider.Value;
        _novelSettings.TextColor = textColor.Tag as string ?? _novelSettings.TextColor;
        _novelSettings.Background = bgColor.Tag as string ?? _novelSettings.Background;
        SanitizeNovelSettings(_novelSettings);
        return true;
    }

    private void FlushNovelSettingsIfDirty(bool commitSpacingDrafts = false, bool requirePersistence = false)
    {
        if (commitSpacingDrafts) CommitNovelSpacingDrafts();
        if (!_novelSettingsLoaded || !_novelSettingsDirty || _novel is null || _closed) return;
        if (!EnsureBookIdCurrent(requirePersistence)) return;
        if (!ReadNovelSettingsFromUi() || !App.Settings.SaveNovelSettingsForBook(_book.Id, _novelSettings))
        {
            if (requirePersistence) throw new IOException("无法保存小说排版，请稍后重试更新。");
            return;
        }
        _novelSettingsDirty = false;
        _rebuildTimer?.Stop();
    }

    private void RebuildNovelDocumentNow()
    {
        double fraction = _novelSubMode == "scroll" ? _novelScrollFraction
            : _chapterPages.TryGetValue(_novelChapter, out int pc) && pc > 1 ? (double)_novelPage / (pc - 1) : 0;

        var builder = new NovelDocumentBuilder(_imageResolver);
        var doc = builder.Build(_novel!.Chapters[_novelChapter], _novelSettings);
        BeginNovelScrollRestore();
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
            if (_closed || !ReferenceEquals(doc, NovelPaged.Document ?? NovelScroll.Document)) return;
            if (_novelSubMode == "scroll")
            {
                _novelScrollFraction = fraction;
                RestoreNovelScrollFraction();
            }
            else
            {
                WhenNovelPaginationReady(pages =>
                {
                    _novelPage = (int)Math.Round(Math.Clamp(fraction, 0, 1) * (pages - 1));
                    GoToNovelPage();
                    UpdateNovelInfo();
                });
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
        bool wasLoading = _loadingNovelUi;
        _loadingNovelUi = true;
        try
        {
            SelectComboByTag(BgColorCombo, _novelSettings.Background);
            SelectComboByTag(TextColorCombo, _novelSettings.TextColor);
        }
        finally
        {
            _loadingNovelUi = wasLoading;
        }
        // 主题只调整当前窗口的默认颜色，不能把本书的字号/字体等局部排版写成全局默认。
        if (rebuild)
            RebuildNovelDocumentNow();
    }

    private void Encoding_Changed(object sender, RoutedEventArgs e)
    {
        // 打开小说时 LoadNovelSettingsUi 会给 EncodingCombo 赋初值，这里也会被触发。
        // 那条路径必须忽略：否则 ReloadTxtAsync 会在 InitNovelAsync 恢复章节之前
        // 重新解析并 LoadNovelChapter(0)，把刚恢复的进度（章节/滚动位置）全部覆盖掉。
        if (_loadingNovelUi || !IsLoaded || _closed) return;
        if (_book.Format == BookFormat.Epub || EncodingCombo.SelectedIndex < 0) return;
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
        CloseNovelSearch();
        longNovelTrack.Add($"ReloadTxtAsync 进入(旧ch={_novelChapter})");
        byte[] bytes = await Task.Run(() => File.ReadAllBytes(_book.FilePath));
        // 重新解析要占用几百毫秒，期间用户可能已经手动换章/关窗：
        // 这里把"重新解析开始前"的章节记下来，不要无条件回到第 0 章。
        if (_closed || _novel is null) return;
        int keepChapter = _novelChapter;
        var parsed = TxtParser.Parse(_txtEncoding is null ? EncodingDetector.Detect(bytes).GetString(bytes) : _txtEncoding.GetString(bytes), _book.Title);
        if (_closed) return;
        CloseNovelSearch(); // 编码重载期间若重新打开了查找，也不能保留旧解析对象的结果。
        _novel = parsed;
        ChapterList.ItemsSource = _novel.Chapters.Select((c, i) => $"{i + 1}. {c.Title}").ToList();
        _chapterPages.Clear();
        LoadNovelChapter(Math.Clamp(keepChapter, 0, Math.Max(0, _novel.Chapters.Count - 1)));
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
                if (NovelScrollStep(+1)) return;   // 一章读完自动接着下一章
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

    /// <summary>章节边界衔接：delta = +1 下一章（从顶部开始），-1 上一章（停在章末）。返回是否发生了切换。</summary>
    private bool NovelScrollStep(int delta)
    {
        if (_novel is null || _novelSubMode != "scroll") return false;
        var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (sv is null) return false;
        double height = sv.ScrollableHeight;
        bool fitsOnScreen = height <= 1;                     // 本章短到不需要滚动：上下都算边界
        bool atBottom = fitsOnScreen || height - sv.VerticalOffset < 3;
        bool atTop = fitsOnScreen || sv.VerticalOffset < 3;
        if (delta > 0 && atBottom)
        {
            if (_novelChapter >= _novel.Chapters.Count - 1) return false;
            LoadNovelChapter(_novelChapter + 1);
            return true;
        }
        if (delta < 0 && atTop)
        {
            if (_novelChapter <= 0) return false;
            _novelScrollFraction = 1.0;                       // 上一章停在末尾
            LoadNovelChapter(_novelChapter - 1, resetPosition: false);
            return true;
        }
        return false;
    }

    internal bool TestNovelScrollStep(int delta) => NovelScrollStep(delta);
    internal void TestNovelGoToChapter(int index) => LoadNovelChapter(index);
    internal void TestNovelScrollToEdge(bool bottom)
    {
        var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (sv is null) return;
        sv.ScrollToVerticalOffset(bottom ? sv.ScrollableHeight : 0);
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
        SynchronizeNovelPageFactsNow();
        int page;
        if (_book.Type == BookType.Comic)
        {
            CaptureCurrentComicPresentation();
            if (_comicPresentation is not { } presented) return;
            page = presented.Page;
        }
        else page = _novelPage;
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
        var source = e.OriginalSource as DependencyObject;
        if (source is null || IsFromBookmarkAction(source)) return;
        if (ItemsControl.ContainerFromElement(BookmarkList, source) is not ListBoxItem container
            || container.DataContext is not BookmarkItem item) return;
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
                    GoToNovelPage();
            });
        }
    }

    private bool IsFromBookmarkAction(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, BookmarkList))
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                System.Windows.Media.Visual => System.Windows.Media.VisualTreeHelper.GetParent(source),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return false;
    }

    private void ScheduleSave()
    {
        if (_closed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow(bool requirePersistence = false)
    {
        SynchronizeNovelPageFactsNow();
        _saveTimer.Stop();
        if (_closed || !_interactionReady || _book.Id <= 0) return;
        // 书可能已在书架里被"移出"或重建：重新绑定到现存的行，否则进度会静默地永远存不进去
        if (!EnsureBookIdCurrent(requirePersistence)) return;
        int page = _page;
        int chapter = 0;
        double scroll = 0;
        double percent;
        string mode = _mode == "novel" ? "novel:" + _novelSubMode : _mode;
        double zoom = 0;

        if (_mode == "novel" && _novel is not null)
        {
            chapter = _novelChapter;
            page = _novelPage;
            if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                scroll = _restoringNovelScrollPosition || sv is null || sv.ScrollableHeight <= 0
                    ? _novelScrollFraction : sv.VerticalOffset / sv.ScrollableHeight;
            }
            double fraction = GetNovelChapterReadFraction(scroll);
            percent = Math.Clamp((_novelChapter + fraction) / _novel.Chapters.Count, 0, 1);
        }
        else if (_loader is not null)
        {
            CaptureCurrentComicPresentation();
            // No content has succeeded in this window: preserve its history.
            // Otherwise flush even an unsaved success before a later failure.
            if (_comicPresentation is not { } presented) return;
            page = presented.Page;
            scroll = presented.Scroll;
            percent = presented.Percent;
            mode = presented.Mode;
            zoom = presented.Zoom;
        }
        else
        {
            percent = _book.Progress;
        }

        try
        {
            App.Library.SaveProgress(_book.Id, percent, page, scroll, chapter, mode, zoom);
        }
        catch (Exception ex)
        {
            Log.Error("保存进度失败", ex);
            if (requirePersistence) throw;
        }
    }

    /// <summary>
    /// 校验 <see cref="_book"/>.Id 仍指向库中存在的行；若已被删除/重建，则按路径重新绑定。
    /// 返回 false 表示这本书确实已不在书架中，本次不保存（应停止后续自动保存）。
    /// </summary>
    private bool EnsureBookIdCurrent(bool requirePersistence = false)
    {
        try
        {
            if (App.Library.GetBook(_book.Id) is not null) return true;
            var live = App.Library.GetBookByPath(Path.GetFullPath(_book.FilePath));
            if (live is null) return false;
            _book.Id = live.Id;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("重新绑定书库条目失败", ex);
            if (requirePersistence) throw;
            return false;
        }
    }

    /// <summary>该书在书库中的 id（供主窗口在重置进度/移出书架前定位要关闭的阅读窗口）。</summary>
    public long BookId => _book.Id;

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
            int count = _novel!.Chapters.Count;
            int chapter = Math.Clamp((int)(fraction * count), 0, count - 1);
            double withinChapter = Math.Clamp(fraction * count - chapter, 0, 1);
            _novelScrollFraction = withinChapter;
            if (chapter != _novelChapter)
            {
                if (_novelSubMode == "paged")
                    _pendingNovelPageFraction = withinChapter;
                LoadNovelChapter(chapter, resetPosition: false);
            }
            else if (_novelSubMode == "scroll")
            {
                var sv = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
                if (sv is not null)
                    sv.ScrollToVerticalOffset(withinChapter * Math.Max(0, sv.ScrollableHeight));
                UpdateNovelInfo();
            }
            else
            {
                WhenNovelPaginationReady(pages =>
                {
                    _novelPage = (int)Math.Round(withinChapter * (pages - 1));
                    GoToNovelPage();
                    UpdateNovelInfo();
                });
            }
        }
        else if (_loader is not null)
        {
            int target = (int)(fraction * _loader.PageCount);
            target = Math.Clamp(target, 0, _loader.PageCount - 1);
            if (_mode == "webtoon")
            {
                NavigateWebtoonToFraction(fraction);
                UpdateComicInfo();
            }
            else if (_mode == "double")
            {
                CancelDoubleNavigation();
                _spreadStart = target;
                RenderDoubleSpread();
            }
            else LoadPageAsync(target);
        }
    }

    #endregion

    #region 通用

    internal void SaveBeforeUpdate()
    {
        FlushNovelSettingsIfDirty(commitSpacingDrafts: true, requirePersistence: true);
        SaveNow(requirePersistence: true);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        CloseComicPageJump(restoreFocus: false);
        CancelDoubleNavigation();
        _webtoonEntryVersion++;
        CloseNovelSearch();
        ResetNovelWheelTarget();
        // 关闭可能早于 350ms 重排防抖，先读控件的最后选择并保存该书排版。
        try { FlushNovelSettingsIfDirty(commitSpacingDrafts: true); } catch (Exception ex) { Log.Error("关闭时保存小说排版失败", ex); }
        // 先保存（含模式/缩放），再标记关闭，避免 SaveNow 被提前拦截
        try { SaveNow(); } catch (Exception ex) { Log.Error("关闭时保存进度失败", ex); }
        _closed = true;
        _comicPendingNavigation = null;
        CancelNovelPageNavigation();
        CancelNovelPaginationWait();
        _saveTimer.Stop();
        // _rebuildTimer 此前完全没停：关闭后 tick 仍会访问已销毁的控件
        _rebuildTimer?.Stop();
        _rebuildTimer = null;
        StopAutoScrollCore();
        _hideBarsTimer?.Stop();
        _hideBarsTimer = null;
        _pageLoadCts?.Cancel();
        _doubleLoadCts?.Cancel();
        // 关窗后把本书的解码页从共享缓存里清掉，否则多本大书开过之后缓存塞满已关书的页，
        // 下一本书首读会全程 LRU 抖动。
        try
        {
            if (_loader is not null) App.SharedCache.Clear(_loader.SourcePath);
        }
        catch (Exception ex)
        {
            Log.Error("清理图片缓存失败", ex);
        }
        try { _loader?.Dispose(); } catch { }
        try { _epubZip?.Dispose(); } catch { }
    }

    /// <summary>停止自动滚动，并且不再通过再次触发 Toggle 的 Checked/Unchecked 来重入。</summary>
    private void StopAutoScrollCore()
    {
        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
        if (AutoScrollToggle is not null && AutoScrollToggle.IsChecked == true)
            AutoScrollToggle.IsChecked = false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        // 先保留尚未重排的用户改动，再做只影响显示的主题颜色转换。
        FlushNovelSettingsIfDirty(commitSpacingDrafts: true);
        ThemeService.Toggle();
        App.Settings.Theme = ThemeService.Current;
        ReaderThemeGlyph.Text = ThemeService.Current == "dark" ? "\uE708" : "\uE706";
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
        if (IsComicPageJumpOpen) return;
        _barsVisible = !_barsVisible;
        TopBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsComicPageJumpOpen) return;
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
        if (_mode == "novel" || IsComicPageJumpOpen) return;
        if (_barsVisible && !IsMouseOver)
            ToggleBars();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsComicPageJumpOpen)
        {
            PageJump_PreviewKeyDown(e);
            return; // Text editing and Ctrl+G routing must precede reading shortcuts.
        }
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_interactionReady && _mode == "novel") OpenNovelSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox spacingInput &&
            (ReferenceEquals(spacingInput, ParagraphSpacingInput) || ReferenceEquals(spacingInput, MarginInput)))
        {
            SyncNovelSpacingInput(ReferenceEquals(spacingInput, ParagraphSpacingInput) ? ParagraphSpacingSlider : MarginSlider);
            e.Handled = true;
            return;
        }
        if (!_interactionReady && e.Key != Key.Escape) return;
        bool inputFocused = IsKeyboardInputFocused();
        if (inputFocused && e.Key is not (Key.Escape or Key.Left or Key.Right)) return;
        if (_mode == "webtoon" && (!WebtoonView.IsReady || _webtoonRestoringPosition)
            && e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Space)
        {
            e.Handled = true;
            return;
        }

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
                if (IsKeyboardInputFocused()) break;   // 交给当前输入/列表控件自己处理
                NavigateHorizontal(isLeftKey: true);
                e.Handled = true;
                break;
            case Key.Right:
                if (IsKeyboardInputFocused()) break;
                NavigateHorizontal(isLeftKey: false);
                e.Handled = true;
                break;
            case Key.Home:
                if (_mode == "webtoon") NavigateWebtoonToPage(0);
                else if (_mode == "double") { CancelDoubleNavigation(); _spreadStart = 0; RenderDoubleSpread(); }
                else if (_mode == "novel") LoadNovelChapter(0);
                else LoadPageAsync(0);
                e.Handled = true;
                break;
            case Key.End:
                if (_mode == "webtoon") NavigateWebtoonToFraction(1);
                else if (_mode == "double") { CancelDoubleNavigation(); _spreadStart = _loader!.PageCount - 1; RenderDoubleSpread(); }
                else if (_mode == "novel")
                {
                    int last = _novel!.Chapters.Count - 1;
                    _novelPendingLastPage = last;
                    _novelScrollFraction = 1;
                    LoadNovelChapter(last, resetPosition: false);
                }
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
        SetInteractionReady(false);
        ComicPagedView.Visibility = WebtoonView.Visibility = DoubleView.Visibility = NovelView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }

    private void SetInteractionReady(bool ready)
    {
        _interactionReady = ready;
        ModePaged.IsEnabled = ModeWebtoon.IsEnabled = ModeDouble.IsEnabled = ready;
        RtlButton.IsEnabled = ready;
        bool positionReady = ready && (_mode != "webtoon" || (WebtoonView.IsReady && !_webtoonRestoringPosition));
        PrevButton.IsEnabled = NextButton.IsEnabled = positionReady;
        BookmarkButton.IsEnabled = positionReady && (_book.Type != BookType.Comic || _comicPresentation is not null);
        NovelSearchButton.IsEnabled = ready;
        ProgressSlider.IsEnabled = positionReady;
        UpdateComicPageJumpAvailability();
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
