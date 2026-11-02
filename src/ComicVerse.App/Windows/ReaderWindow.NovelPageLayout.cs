using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    private bool _updatingNovelPagePadding;
    private ControlTemplate? _novelNavigationTemplate;
    private bool _novelNavigationContrastApplied;
    private FlowDocument? _lastStableNovelPageDocument;
    private NovelPageLayoutState _lastStableNovelPageLayout;
    private FlowDocument? _novelPageNavigationInitializedDocument;
    private FlowDocument? _novelExplicitPageDocument;
    private int _novelExplicitPageTarget = -1;
    private NovelPageLayoutState _novelExplicitPageLayout;
    private bool _novelExplicitPageLayoutKnown;
    private long _novelExplicitPageVersion;
    private long _novelPageReflowVersion;
    private DispatcherOperation? _novelPageReflowOperation;

    private readonly record struct NovelPageLayoutState(
        Size PageSize, Thickness Padding, double FontSize, double LineHeight, double Zoom, int Pages);

    internal double NovelEffectiveTopMargin => (NovelPaged.Document as FlowDocument)?.PagePadding.Top ?? double.NaN;

    private void NovelPaged_LayoutUpdated(object? sender, EventArgs e)
    {
        EnsureNovelPagePadding();
        SynchronizeNovelPageAfterReflow();
        ApplyNovelNativeNavigationContrast();
    }

    private bool TryGetNovelPagePadding(out FlowDocument? document, out Thickness padding)
    {
        document = NovelPaged.Document as FlowDocument;
        padding = default;
        if (_closed || _mode != "novel" || _novelSubMode != "paged" || document is null
            || !NovelPaged.IsVisible || NovelPaged.ActualHeight <= 0) return false;
        double height = ((IDocumentPaginatorSource)document).DocumentPaginator.PageSize.Height;
        if (!double.IsFinite(height) || height <= 0) return false;

        double requested = _novelSettings.PageMargin;
        double lineHeight = document.LineHeight;
        if (!double.IsFinite(lineHeight) || lineHeight <= 0)
            lineHeight = document.FontSize * _novelSettings.LineSpacing;
        double bodyHeight = Math.Min(height, Math.Max(1, lineHeight) * 3);
        double vertical = Math.Min(requested, Math.Max(0, (height - bodyHeight) / 2));
        padding = new Thickness(requested, vertical, requested, vertical);
        return true;
    }

    private static bool NovelPagePaddingMatches(Thickness current, Thickness expected)
        => Math.Abs(current.Left - expected.Left) < 0.01 && Math.Abs(current.Right - expected.Right) < 0.01
            && Math.Abs(current.Top - expected.Top) < 0.01 && Math.Abs(current.Bottom - expected.Bottom) < 0.01;

    private bool IsNovelPagePaddingCurrent()
        => TryGetNovelPagePadding(out var document, out var padding) && NovelPagePaddingMatches(document!.PagePadding, padding);

    private void EnsureNovelPagePadding()
    {
        if (_updatingNovelPagePadding || !TryGetNovelPagePadding(out var document, out var padding)
            || NovelPagePaddingMatches(document!.PagePadding, padding)) return;
        _updatingNovelPagePadding = true;
        try
        {
            // 仅约束当前文档的有效上下留白；用户的滑块、输入及按书设置继续保存原始请求值。
            document.PagePadding = padding;
        }
        finally
        {
            _updatingNovelPagePadding = false;
        }
    }

    private void ApplyNovelNativeNavigationContrast()
    {
        if (_closed || !NovelPaged.IsVisible || NovelPaged.Template is null
            || (_novelNavigationContrastApplied && ReferenceEquals(_novelNavigationTemplate, NovelPaged.Template))) return;
        bool found = false;
        // 只调整现有模板里的导航控件颜色，不替换模板或移除分页/选择所需的部件。
        foreach (var child in EnumerateNovelViewerVisuals(NovelPaged))
        {
            if (child is ButtonBase button)
            {
                button.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                button.SetResourceReference(Control.BackgroundProperty, "SurfaceAltBrush");
                button.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
                button.Opacity = button.IsEnabled ? 1 : 0.4;
                button.IsEnabledChanged += NovelNativeNavigation_IsEnabledChanged;
                found = true;
            }
            else if (child is TextBlock label)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                found = true;
            }
            else if (child is System.Windows.Shapes.Path path && path.Fill is SolidColorBrush brush
                && brush.Color == Colors.Black)
            {
                path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextPrimaryBrush");
            }
        }
        _novelNavigationTemplate = NovelPaged.Template;
        _novelNavigationContrastApplied = found;
    }

    private void SynchronizeNovelPageAfterReflow()
    {
        if (!TryGetStableNovelPageLayout(out var document, out var layout)) return;
        if (SettleNovelExplicitPageNavigation(document!, layout)) return;
        // 首次载入/恢复必须先完成统一 GoTo，不能用新文档暂时显示的首页覆盖保存位置。
        if (!ReferenceEquals(_novelPageNavigationInitializedDocument, document)) return;
        bool reflow = !ReferenceEquals(_lastStableNovelPageDocument, document)
            || !NovelPageLayoutsMatch(_lastStableNovelPageLayout, layout);
        bool nativeNavigation = NovelPaged.MasterPageNumber > 0 && NovelPaged.MasterPageNumber - 1 != _novelPage;
        if (!reflow && !nativeNavigation) return;
        QueueNovelPageFactSynchronization(document!, layout);
    }

    private bool TryGetStableNovelPageLayout(out FlowDocument? document, out NovelPageLayoutState layout)
    {
        document = NovelPaged.Document as FlowDocument;
        layout = default;
        if (_closed || !_interactionReady || _mode != "novel" || _novelSubMode != "paged"
            || document is null || _novelPageLayoutPending || !HasStableNovelChapterLayout()) return false;
        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        if (paginator.PageCount <= 0) return false;
        layout = new NovelPageLayoutState(paginator.PageSize, document.PagePadding, document.FontSize,
            document.LineHeight, NovelPaged.Zoom, paginator.PageCount);
        return true;
    }

    private static bool NovelPageLayoutsMatch(NovelPageLayoutState a, NovelPageLayoutState b)
        => Math.Abs(a.PageSize.Width - b.PageSize.Width) < 0.01
            && Math.Abs(a.PageSize.Height - b.PageSize.Height) < 0.01
            && NovelPagePaddingMatches(a.Padding, b.Padding)
            && Math.Abs(a.FontSize - b.FontSize) < 0.01
            && (a.LineHeight.Equals(b.LineHeight) || Math.Abs(a.LineHeight - b.LineHeight) < 0.01)
            && Math.Abs(a.Zoom - b.Zoom) < 0.01 && a.Pages == b.Pages;

    private void AbortNovelPageFactSynchronization()
    {
        _novelPageReflowVersion++;
        _novelPageReflowOperation?.Abort();
        _novelPageReflowOperation = null;
    }

    private void RegisterNovelExplicitPageNavigation(FlowDocument document, int target)
    {
        AbortNovelPageFactSynchronization();
        _novelExplicitPageVersion++;
        _novelExplicitPageDocument = document;
        _novelExplicitPageTarget = Math.Max(0, target);
        _novelExplicitPageLayoutKnown = TryGetStableNovelPageLayout(out var current, out _novelExplicitPageLayout)
            && ReferenceEquals(current, document);
    }

    private void CancelNovelPageNavigation()
    {
        AbortNovelPageFactSynchronization();
        _novelExplicitPageVersion++;
        _novelExplicitPageDocument = null;
        _novelExplicitPageTarget = -1;
        _novelExplicitPageLayoutKnown = false;
        _novelPageNavigationInitializedDocument = null;
        _lastStableNovelPageDocument = null;
        _lastStableNovelPageLayout = default;
    }

    private void SettleNovelExplicitPageNavigation()
    {
        if (TryGetStableNovelPageLayout(out var document, out var layout))
            SettleNovelExplicitPageNavigation(document!, layout);
    }

    private bool SettleNovelExplicitPageNavigation(FlowDocument document, NovelPageLayoutState layout)
    {
        if (_novelExplicitPageDocument is null) return false;
        if (!ReferenceEquals(_novelExplicitPageDocument, document))
        {
            CancelNovelPageNavigation();
            return false;
        }
        int target = Math.Clamp(_novelExplicitPageTarget, 0, layout.Pages - 1);
        if (target != _novelExplicitPageTarget)
        {
            // 导航尚未落地就遇到缩页：在同一 intent 内夹紧并重发，不采纳 WPF 的临时首页。
            _novelExplicitPageTarget = target;
            _novelPage = target;
            _novelExplicitPageLayout = layout;
            _novelExplicitPageLayoutKnown = true;
            NovelPaged.GoToPage(target + 1);
            UpdateNovelInfo();
            return true;
        }
        if (NovelPaged.MasterPageNumber != target + 1)
        {
            // 目标仍合法但尚未落地时遇到新布局，也应在该签名下重发一次；不能永久卡在旧请求。
            if (!_novelExplicitPageLayoutKnown || !NovelPageLayoutsMatch(_novelExplicitPageLayout, layout))
            {
                _novelExplicitPageLayout = layout;
                _novelExplicitPageLayoutKnown = true;
                NovelPaged.GoToPage(target + 1);
            }
            return true;
        }
        _novelPage = target;
        _novelExplicitPageDocument = null;
        _novelExplicitPageTarget = -1;
        _novelExplicitPageLayoutKnown = false;
        _novelPageNavigationInitializedDocument = document;
        RecordStableNovelPageLayout(document, layout);
        return true;
    }

    private void RecordStableNovelPageLayout(FlowDocument document, NovelPageLayoutState layout)
    {
        _lastStableNovelPageDocument = document;
        _lastStableNovelPageLayout = layout;
        _chapterPages[_novelChapter] = layout.Pages;
    }

    private void QueueNovelPageFactSynchronization(FlowDocument document, NovelPageLayoutState expected)
    {
        // 新布局替换旧队列，不能仅使旧回调失效却漏掉最后一次重排。
        AbortNovelPageFactSynchronization();
        long reflowVersion = _novelPageReflowVersion;
        long navigationVersion = _novelExplicitPageVersion;
        _novelPageReflowOperation = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (reflowVersion != _novelPageReflowVersion) return;
            _novelPageReflowOperation = null;
            if (navigationVersion != _novelExplicitPageVersion || _novelExplicitPageDocument is not null
                || !ReferenceEquals(_novelPageNavigationInitializedDocument, document)
                || !TryGetStableNovelPageLayout(out var currentDocument, out var current)
                || !ReferenceEquals(currentDocument, document)) return;
            if (!NovelPageLayoutsMatch(expected, current))
            {
                QueueNovelPageFactSynchronization(document, current);
                return;
            }
            AdoptNovelDisplayedPage(document, current);
        }));
    }

    private void AdoptNovelDisplayedPage(FlowDocument document, NovelPageLayoutState layout)
    {
        if (ReferenceEquals(_lastStableNovelPageDocument, document)
            && layout.Pages < _lastStableNovelPageLayout.Pages
            && _novelPage == _lastStableNovelPageLayout.Pages - 1
            && NovelPaged.MasterPageNumber != layout.Pages)
        {
            // 保留“正在读末页”的意图；中间页重排仍以 WPF 保持的实际文本位置为准。
            _novelPage = layout.Pages - 1;
            GoToNovelPage();
            UpdateNovelInfo();
            return;
        }
        int displayed = NovelPaged.MasterPageNumber;
        if (displayed < 1 || displayed > layout.Pages) return;
        RecordStableNovelPageLayout(document, layout);
        if (_novelPage == displayed - 1) return;
        _novelPage = displayed - 1;
        UpdateNovelInfo();
    }

    private void SynchronizeNovelPageFactsNow()
    {
        if (!TryGetStableNovelPageLayout(out var document, out var layout)
            || SettleNovelExplicitPageNavigation(document!, layout)
            || !ReferenceEquals(_novelPageNavigationInitializedDocument, document)) return;
        // 关闭/保存可能先于 ContextIdle；最终布局已有效且没有显式目标时，直接采纳当前可见页。
        AbortNovelPageFactSynchronization();
        AdoptNovelDisplayedPage(document!, layout);
    }

    private static void NovelNativeNavigation_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ButtonBase button) button.Opacity = button.IsEnabled ? 1 : 0.4;
    }

    private static IEnumerable<DependencyObject> EnumerateNovelViewerVisuals(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in EnumerateNovelViewerVisuals(child)) yield return descendant;
        }
    }
}
