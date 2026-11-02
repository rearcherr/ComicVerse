using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ComicVerse.App.Helpers;
using ComicVerse.Core;
using ComicVerse.Core.Novel;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    private NovelSearchWindow? _novelSearchWindow;
    private CancellationTokenSource? _novelSearchNavigationCts;
    private long _novelSearchNavigationVersion;
    private FlowDocument? _novelSearchSelectionDocument;
    private Paragraph? _novelSearchSelectionParagraph;
    private int _novelSearchLocatedPageIndex = -1;
    private int _novelSearchLocatedBlockIndex = -1;

    internal NovelSearchWindow TestOpenNovelSearch() => OpenNovelSearch();
    internal string NovelSearchSelectedText =>
        (_novelSubMode == "scroll" ? NovelScroll.Selection : NovelPaged.Selection)?.Text ?? "";
    internal int NovelSearchLocatedPageIndex => _novelSearchLocatedPageIndex;
    internal int NovelSearchLocatedBlockIndex => _novelSearchLocatedBlockIndex;
    internal int NovelSearchSelectionOffset
    {
        get
        {
            if (_novelSearchSelectionParagraph is null ||
                !ReferenceEquals(_novelSearchSelectionDocument, NovelPaged.Document ?? NovelScroll.Document)) return -1;
            var selection = _novelSubMode == "scroll" ? NovelScroll.Selection : NovelPaged.Selection;
            return selection is null ? -1
                : new TextRange(_novelSearchSelectionParagraph.ContentStart, selection.Start).Text.Length;
        }
    }
    /// <summary>检查命中起始字符的整行确实在当前视口中，而不只检查选择文本或滚动偏移。</summary>
    internal bool NovelSearchSelectionInViewport
        => ReferenceEquals(_novelSearchSelectionDocument, NovelPaged.Document ?? NovelScroll.Document)
            && (_novelSubMode == "paged"
                ? _novelSearchLocatedPageIndex >= 0 && NovelPaged.MasterPageNumber == _novelSearchLocatedPageIndex + 1
                : TryGetNovelSearchStartInViewport(out _, out var rect, out var viewport) && IsNovelSearchLineVisible(rect, viewport));
    internal string NovelSearchSelectionViewportDetail
        => TryGetNovelSearchStartInViewport(out var scroll, out var rect, out var viewport)
            ? $"character={rect}; viewport={viewport}; offset={scroll!.VerticalOffset:F1}/{scroll.ScrollableHeight:F1}"
            : "selection layout unavailable";

    private void NovelSearchButton_Click(object sender, RoutedEventArgs e) => OpenNovelSearch();

    private NovelSearchWindow OpenNovelSearch()
    {
        if (_closed || !_interactionReady || _mode != "novel" || _novel is null || _novel.Chapters.Count == 0)
            throw new InvalidOperationException("小说尚未加载完成");
        if (_novelSearchWindow is not null)
        {
            _novelSearchWindow.Activate();
            _novelSearchWindow.FocusQuery();
            return _novelSearchWindow;
        }
        var book = _novel;
        var window = new NovelSearchWindow(book, (match, ct) => NavigateNovelSearchMatchAsync(book, match, ct))
        {
            Owner = this,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _novelSearchWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_novelSearchWindow, window)) return;
            _novelSearchWindow = null;
            CancelNovelSearchNavigation();
        };
        window.Show();
        window.FocusQuery();
        return window;
    }

    private void CancelNovelSearchNavigation()
    {
        _novelSearchNavigationVersion++;
        _novelSearchNavigationCts?.Cancel();
    }

    private void CloseNovelSearch()
    {
        CancelNovelSearchNavigation();
        var window = _novelSearchWindow;
        _novelSearchWindow = null;
        window?.Close();
        _novelSearchSelectionDocument = null;
        _novelSearchSelectionParagraph = null;
        _novelSearchLocatedPageIndex = _novelSearchLocatedBlockIndex = -1;
    }

    private async Task<bool> NavigateNovelSearchMatchAsync(NovelBook book, NovelSearchMatch match, CancellationToken ct)
    {
        CancelNovelSearchNavigation();
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _novelSearchNavigationCts = navigation;
        long version = _novelSearchNavigationVersion;
        bool IsCurrent() => !_closed && !navigation.IsCancellationRequested && version == _novelSearchNavigationVersion
            && ReferenceEquals(book, _novel) && _mode == "novel";
        try
        {
            if (!IsCurrent() || match.ChapterIndex < 0 || match.ChapterIndex >= book.Chapters.Count) return false;
            var chapter = book.Chapters[match.ChapterIndex];
            if (match.BlockIndex < 0 || match.BlockIndex >= chapter.Blocks.Count) return false;
            var block = chapter.Blocks[match.BlockIndex];
            if (block.IsImage || string.IsNullOrWhiteSpace(block.Text) || match.Start < 0 || match.Length <= 0
                || match.Start > block.Text.Length - match.Length) return false;

            _novelSearchSelectionDocument = null;
            _novelSearchSelectionParagraph = null;
            _novelSearchLocatedPageIndex = _novelSearchLocatedBlockIndex = -1;
            FlushNovelSettingsIfDirty(commitSpacingDrafts: true);
            StopAutoScrollCore();
            LoadNovelChapter(match.ChapterIndex);
            var document = (NovelPaged.Document ?? NovelScroll.Document) as FlowDocument;
            if (document is null) return false;
            bool StillCurrent() => IsCurrent() && _novelChapter == match.ChapterIndex
                && ReferenceEquals(document, NovelPaged.Document ?? NovelScroll.Document);
            if (!await WaitForNovelSearchLayoutAsync(StillCurrent, navigation.Token)) return false;

            // Builder 为每个非空文本块生成一个 Paragraph，图片块不参与这个序列。
            int paragraphIndex = chapter.Blocks.Take(match.BlockIndex)
                .Count(b => !b.IsImage && !string.IsNullOrWhiteSpace(b.Text));
            var paragraph = document.Blocks.OfType<Paragraph>().ElementAtOrDefault(paragraphIndex);
            var run = paragraph?.Inlines.OfType<Run>().FirstOrDefault();
            if (run is null || !string.Equals(run.Text, block.Text, StringComparison.Ordinal)) return false;
            var start = run.ContentStart.GetPositionAtOffset(match.Start, LogicalDirection.Forward);
            var end = run.ContentStart.GetPositionAtOffset(match.Start + match.Length, LogicalDirection.Backward);
            if (start is null || end is null || !StillCurrent()) return false;

            if (_novelSubMode == "paged")
            {
                var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator as DynamicDocumentPaginator;
                if (paginator is null) return false;
                int page = paginator.GetPageNumber(start);
                if (page < 0 || page >= paginator.PageCount) return false;
                _novelPage = page;
                GoToNovelPage();
                NovelPaged.Selection.Select(start, end);
                _novelSearchLocatedPageIndex = page;
            }
            else
            {
                NovelScroll.Selection.Select(start, end);
                _novelSearchLocatedPageIndex = -1;
                if (!await BringNovelSearchCharacterIntoViewAsync(StillCurrent, navigation.Token)) return false;
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, navigation.Token);
            if (!StillCurrent()) return false;
            _novelSearchSelectionDocument = document;
            _novelSearchSelectionParagraph = paragraph;
            _novelSearchLocatedBlockIndex = match.BlockIndex;
            UpdateNovelInfo();
            return string.Equals(NovelSearchSelectedText, block.Text.Substring(match.Start, match.Length), StringComparison.Ordinal)
                && NovelSearchSelectionInViewport;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrent()) Log.Error("定位小说搜索结果失败", ex);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_novelSearchNavigationCts, navigation)) _novelSearchNavigationCts = null;
        }
    }

    private static bool IsNovelSearchLineVisible(Rect character, Rect viewport)
        => !character.IsEmpty && character.Height > 0 && viewport.Width > 0 && viewport.Height > 0
            && character.Left >= viewport.Left - 1 && character.Left <= viewport.Right + 1
            && character.Top >= viewport.Top - 1 && character.Bottom <= viewport.Bottom + 1;

    private bool TryGetNovelSearchStartInViewport(out ScrollViewer? scroll, out Rect character, out Rect viewport)
    {
        scroll = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        character = viewport = Rect.Empty;
        if (_novelSubMode != "scroll" || NovelScroll.Document is null || scroll is null
            || NovelScroll.Selection is null || NovelScroll.Selection.IsEmpty
            || scroll.Content is not FrameworkElement renderScope) return false;
        var presenter = VisualTreeHelpers.FindVisualChild<ScrollContentPresenter>(scroll);
        if (presenter is null || presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0) return false;
        try
        {
            // GetCharacterRect 使用文本呈现宿主的 UI 坐标；用公开的模板父级和 Visual 变换
            // 将其转成 ScrollContentPresenter 坐标，不能直接拿文档 y 与视口高度比较。
            Visual coordinateScope = renderScope.TemplatedParent as Visual
                ?? (renderScope.Parent as FrameworkElement)?.TemplatedParent as Visual
                ?? renderScope;
            var rect = NovelScroll.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return false;
            character = coordinateScope.TransformToVisual(presenter).TransformBounds(rect);
            viewport = new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> BringNovelSearchCharacterIntoViewAsync(Func<bool> isCurrent, CancellationToken ct)
    {
        // 只定位段落会漏掉数万字单段中的后部命中；按真实字符矩形调整滚动位置。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Dispatcher.InvokeAsync(() => NovelScroll.UpdateLayout(), DispatcherPriority.Loaded, ct);
            if (!isCurrent() || !TryGetNovelSearchStartInViewport(out var scroll, out var rect, out var viewport)) return false;
            if (IsNovelSearchLineVisible(rect, viewport)) return true;
            double offset = scroll!.VerticalOffset + rect.Top - viewport.Height * 0.25;
            scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, scroll.ScrollableHeight)));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, ct);
        }
        return isCurrent() && TryGetNovelSearchStartInViewport(out _, out var finalRect, out var finalViewport)
            && IsNovelSearchLineVisible(finalRect, finalViewport);
    }

    private async Task<bool> WaitForNovelSearchLayoutAsync(Func<bool> isCurrent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Probe()
        {
            if (!isCurrent()) completion.TrySetResult(false);
            else if (HasStableNovelChapterLayout()) completion.TrySetResult(true);
        }
        EventHandler handler = (_, _) => Probe();
        LayoutUpdated += handler;
        using var cancellation = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!isCurrent()) { completion.TrySetResult(false); return; }
                if (_novelSubMode == "scroll") NovelScroll.UpdateLayout();
                else NovelPaged.UpdateLayout();
                Probe();
            }, DispatcherPriority.Loaded, timeout.Token);
            if (!completion.Task.IsCompleted)
                await Dispatcher.InvokeAsync((Action)Probe, DispatcherPriority.ContextIdle, timeout.Token);
            return await completion.Task;
        }
        finally
        {
            LayoutUpdated -= handler;
        }
    }
}
