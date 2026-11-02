using System.Windows.Controls;
using ComicVerse.App.Helpers;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    // Navigation cursors are requests. A failed or superseded request must not
    // replace the last complete presentation, even before its debounce saves.
    private sealed record ComicPresentation(string Mode, int Page, int VisiblePages,
        double Scroll, double Percent, double Zoom);

    private ComicPresentation? _comicPresentation;
    private int _comicInitialPage;
    private int _pagedPresentedPage = -1;

    // Only an active request is transferable between modes. Its original page
    // survives a provisional double-spread canonicalization; a failed cursor
    // remains available to Next/Retry but is no longer a transferable intent.
    private sealed record ComicNavigationIntent(long Generation, int Page, double? Fraction);
    private ComicNavigationIntent? _comicPendingNavigation;
    private long _comicNavigationGeneration;

    private ComicNavigationIntent BeginComicNavigation(int page, double? fraction = null)
    {
        int count = _loader?.PageCount ?? 1;
        return _comicPendingNavigation = new ComicNavigationIntent(++_comicNavigationGeneration,
            Math.Clamp(page, 0, Math.Max(0, count - 1)), fraction is double f ? Math.Clamp(f, 0, 1) : null);
    }

    private bool IsCurrentComicNavigation(ComicNavigationIntent intent)
        => ReferenceEquals(_comicPendingNavigation, intent);

    private void FinishComicNavigation(ComicNavigationIntent intent)
    {
        if (IsCurrentComicNavigation(intent)) _comicPendingNavigation = null;
    }

    private ComicNavigationIntent GetComicModeEntryIntent()
    {
        int page = GetComicModeEntryPage();
        if (_comicPendingNavigation is { } pending) return pending;
        double? fraction = _comicPresentation is { Mode: "webtoon" } presented
            ? presented.Scroll : _comicPresentation is null ? _webtoonRestoreFraction : null;
        return BeginComicNavigation(page, fraction);
    }

    private void CommitComicPresentation(string mode, int page, int visiblePages)
    {
        if (_loader is null) return;
        double zoom = mode == "double" ? _doubleAppliedScale : PageScale.ScaleX;
        _comicPresentation = new ComicPresentation(mode, page, visiblePages, 0,
            Math.Clamp((double)(page + visiblePages) / _loader.PageCount, 0, 1), Math.Round(zoom, 3));
        if (mode == "paged") _pagedPresentedPage = page;
        SetInteractionReady(_interactionReady);
    }

    private void CaptureCurrentComicPresentation()
    {
        if (_loader is null) return;
        if (_mode == "webtoon" && WebtoonView.IsReady && !_webtoonRestoringPosition
            && WebtoonView.CurrentPage >= 0)
        {
            if (_comicPendingNavigation is { } pending && !HasReachedWebtoonNavigation(pending)) return;
            _page = WebtoonView.CurrentPage;
            _webtoonFraction = WebtoonView.ScrollFraction;
            _comicPresentation = new ComicPresentation("webtoon", _page, 1,
                _webtoonFraction, Math.Clamp(_webtoonFraction, 0, 1), _zoom);
        }
        else if (_comicPresentation is { } presented && HasRetainedComicPresentation(_mode))
        {
            // Zoom may change while a later request is loading or has failed;
            // it still applies to the retained successful image, not its cursor.
            double zoom = _mode == "double" ? _doubleAppliedScale : PageScale.ScaleX;
            _comicPresentation = presented with { Zoom = Math.Round(zoom, 3) };
        }
    }

    private bool HasRetainedComicPresentation(string mode)
    {
        if (_comicPresentation is not { } presented || presented.Mode != mode) return false;
        if (mode == "paged")
            return PageImage.Source is not null && _pagedPresentedPage == presented.Page;
        if (mode != "double" || LeftImage.Source is null || _doublePageCount != presented.VisiblePages)
            return false;
        if (presented.VisiblePages == 1)
            return _doubleLeftIndex == presented.Page && _doubleRightIndex == -1 && RightImage.Source is null;
        return RightImage.Source is not null
            && ((_doubleLeftIndex == presented.Page && _doubleRightIndex == presented.Page + 1)
                || (_doubleRightIndex == presented.Page && _doubleLeftIndex == presented.Page + 1));
    }

    private int GetComicModeEntryPage()
    {
        CaptureCurrentComicPresentation();
        return _comicPendingNavigation?.Page ?? _comicPresentation?.Page ?? _comicInitialPage;
    }

    private void NavigateWebtoonToPage(int page)
    {
        if (_loader is null) return;
        var intent = BeginComicNavigation(page);
        if (_mode == "webtoon" && (!WebtoonView.IsReady || _webtoonRestoringPosition))
        {
            ShowWebtoon();
            return;
        }
        WebtoonView.ScrollToPage(intent.Page);
        // A no-op navigation emits no position event. Settle it explicitly,
        // while the real offset guard leaves not-yet-applied targets pending.
        UpdateComicInfo();
    }

    private void NavigateWebtoonToFraction(double fraction)
    {
        if (_loader is null) return;
        fraction = Math.Clamp(fraction, 0, 1);
        int page = Math.Clamp((int)(fraction * _loader.PageCount), 0, _loader.PageCount - 1);
        BeginComicNavigation(page, fraction);
        if (_mode == "webtoon" && (!WebtoonView.IsReady || _webtoonRestoringPosition))
        {
            ShowWebtoon();
            return;
        }
        WebtoonView.ScrollToFraction(fraction);
        UpdateComicInfo();
    }

    private void CompletePresentedWebtoonNavigation()
    {
        if (_mode != "webtoon" || !WebtoonView.IsReady || _webtoonRestoringPosition
            || _comicPendingNavigation is not { } pending) return;
        if (HasReachedWebtoonNavigation(pending)) FinishComicNavigation(pending);
    }

    private bool HasReachedWebtoonNavigation(ComicNavigationIntent intent)
    {
        if (!WebtoonView.IsReady) return false;
        if (intent.Fraction is not double fraction) return WebtoonView.HasReachedPageOffset(intent.Page);
        var scroll = VisualTreeHelpers.FindVisualChild<ScrollViewer>(WebtoonView);
        return scroll is { ViewportHeight: > 0 }
            && Math.Abs(scroll.VerticalOffset - fraction * Math.Max(0, scroll.ScrollableHeight)) <= 1;
    }

    private void UpdatePresentedComicInfo()
    {
        if (_loader is null) return;
        if (_comicPresentation is not { } presented)
        {
            PageInfoText.Text = "尚未载入";
            SetSlider(0);
            return;
        }
        int last = presented.Page + presented.VisiblePages;
        PageInfoText.Text = presented.Mode == "double" && presented.VisiblePages > 1
            ? $"{presented.Page + 1}-{last} / {_loader.PageCount}"
            : $"{presented.Page + 1} / {_loader.PageCount}";
        SetSlider(presented.Percent);
    }

    private static string ComicPageRange(int start, int count)
        => count > 1 ? $"第 {start + 1}-{start + count} 页" : $"第 {start + 1} 页";

    private string ComicLoadFailureText(string mode, int start, int count)
    {
        string message = $"{ComicPageRange(start, count)}加载失败，点击重试";
        if (_comicPresentation is { } presented)
        {
            string retained = ComicPageRange(presented.Page, presented.VisiblePages);
            message += HasRetainedComicPresentation(mode) ? $"；仍显示{retained}" : $"；上次成功位置：{retained}";
        }
        return message;
    }
}
