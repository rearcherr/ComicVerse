using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ComicVerse.App.Helpers;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    // WPF 常见默认每格约 3 行 × 16 DIP；取其三倍，仍保留高分辨率滚轮的 delta 比例。
    internal const double NovelWheelPixelsPerNotch = 144;
    private const double NovelWheelEdgeTolerance = 0.5;
    private ScrollViewer? _novelWheelViewer;
    private FlowDocument? _novelWheelDocument;
    private int _novelWheelRestoreVersion;
    private double? _novelWheelTarget;
    private readonly List<double> _novelWheelIssuedTargets = new();

    private void NovelScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != "novel" || _novelSubMode != "scroll") return;

        // 自己消费滚轮，避免系统默认再滚一次；初始化/恢复期间也不能让默认处理改写恢复位置。
        e.Handled = true;
        StopAutoScroll();
        if (e.Delta == 0) return;
        if (_closed || !_interactionReady || _novel is null || _restoringNovelScrollPosition
            || !HasStableNovelChapterLayout())
        {
            ResetNovelWheelTarget();
            return;
        }

        var viewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(NovelScroll);
        if (viewer is null) return;
        if (!ReferenceEquals(viewer, _novelWheelViewer)
            || !ReferenceEquals(NovelScroll.Document, _novelWheelDocument)
            || _novelWheelRestoreVersion != _novelScrollRestoreVersion)
        {
            ResetNovelWheelTarget();
            _novelWheelViewer = viewer;
            _novelWheelDocument = NovelScroll.Document;
            _novelWheelRestoreVersion = _novelScrollRestoreVersion;
            viewer.PreviewMouseDown += NovelWheelExternalInput;
            viewer.PreviewKeyDown += NovelWheelExternalInput;
            viewer.PreviewTouchDown += NovelWheelExternalTouch;
        }

        double height = Math.Max(0, viewer.ScrollableHeight);
        double actual = Math.Clamp(viewer.VerticalOffset, 0, height);
        double basis = Math.Clamp(_novelWheelTarget ?? actual, 0, height);
        bool down = e.Delta < 0;

        // 本格到达边界只落到章尾/章首；实际画面已在边界时再滚，才切到相邻章节。
        // 同时检查累积目标，防止“向上尚未生效、又向下”误用旧的章尾位置接章。
        bool atEdge = down
            ? actual >= height - NovelWheelEdgeTolerance && basis >= height - NovelWheelEdgeTolerance
            : actual <= NovelWheelEdgeTolerance && basis <= NovelWheelEdgeTolerance;
        if (atEdge)
        {
            ResetNovelWheelTarget();
            NovelScrollStep(down ? 1 : -1);
            return;
        }

        double target = Math.Clamp(basis - (double)e.Delta / Mouse.MouseWheelDeltaForOneLine
            * NovelWheelPixelsPerNotch, 0, height);
        _novelWheelTarget = target;
        if (_novelWheelIssuedTargets.Count == 0
            || Math.Abs(_novelWheelIssuedTargets[^1] - target) > NovelWheelEdgeTolerance)
            _novelWheelIssuedTargets.Add(target);
        viewer.ScrollToVerticalOffset(target);
        // ScrollChanged 在布局真正完成后更新比例并安排防抖保存，不能在这里保存旧 offset。
    }

    /// <summary>换章、排版恢复和关窗时丢弃上一文档的未落地滚轮目标。</summary>
    private void ResetNovelWheelTarget()
    {
        if (_novelWheelViewer is not null)
        {
            _novelWheelViewer.PreviewMouseDown -= NovelWheelExternalInput;
            _novelWheelViewer.PreviewKeyDown -= NovelWheelExternalInput;
            _novelWheelViewer.PreviewTouchDown -= NovelWheelExternalTouch;
        }
        _novelWheelViewer = null;
        _novelWheelDocument = null;
        _novelWheelTarget = null;
        _novelWheelIssuedTargets.Clear();
    }

    private void NovelWheelExternalInput(object sender, InputEventArgs e) => ResetNovelWheelTarget();
    private void NovelWheelExternalTouch(object? sender, TouchEventArgs e) => ResetNovelWheelTarget();

    /// <summary>由真实 ScrollChanged 调用，区分本轮尚在落地的命令与滚动条/键盘等外部定位。</summary>
    private void ObserveNovelWheelScroll(ScrollChangedEventArgs e)
    {
        if (_novelWheelViewer is null) return;
        if (_closed || _novelSubMode != "scroll" || _restoringNovelScrollPosition
            || !ReferenceEquals(NovelScroll.Document, _novelWheelDocument)
            || _novelWheelRestoreVersion != _novelScrollRestoreVersion)
        {
            ResetNovelWheelTarget();
            return;
        }
        if (!ReferenceEquals(e.OriginalSource, _novelWheelViewer)) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            ResetNovelWheelTarget();
            return;
        }
        if (e.VerticalChange == 0 || _novelWheelTarget is null) return;

        int issued = _novelWheelIssuedTargets.FindLastIndex(
            offset => Math.Abs(offset - e.VerticalOffset) <= NovelWheelEdgeTolerance);
        if (issued < 0)
        {
            // 外部拖滚动条、键盘或测试定位后，下一格必须从真实的新位置出发。
            ResetNovelWheelTarget();
            return;
        }
        _novelWheelIssuedTargets.RemoveRange(0, issued + 1);
        if (Math.Abs(e.VerticalOffset - _novelWheelTarget.Value) <= NovelWheelEdgeTolerance)
        {
            _novelWheelTarget = null;
            _novelWheelIssuedTargets.Clear();
        }
    }

    /// <summary>冒烟测试注入真实预览滚轮路由，不要求桌面全局输入。</summary>
    internal bool TestNovelMouseWheel(int delta)
    {
        UIElement target = _novelSubMode == "scroll" ? NovelScroll : NovelPaged;
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = target
        };
        target.RaiseEvent(args);
        return args.Handled;
    }
}
