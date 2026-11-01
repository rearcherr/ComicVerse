using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Windows.Threading;
using ComicVerse.Core.Services;

namespace ComicVerse.App.Controls;

/// <summary>
/// 条漫阅读器：按视口范围懒加载/卸载图片，支持平滑惯性滚动、缩放与页号追踪。
/// </summary>
public partial class WebtoonViewer : UserControl
{
    private ComicImageLoader? _loader;
    private readonly List<(int Width, int Height)> _dims = new();
    private readonly List<double> _tops = new();
    private readonly Dictionary<int, Image> _rendered = new();
    private double _scale = 1.0;
    private double _total;
    private bool _layoutReady;
    private int _currentIndex = -1;
    private long _buildVersion;
    private double _lastViewportWidth = -1;
    private double _scrollTarget = double.NaN;
    private int _pendingIndex = -1;
    private readonly Dictionary<int, CancellationTokenSource> _loadCts = new();
    private DispatcherTimer? _resyncTimer;
    private int _resyncTicks;
    private int _visibleFirst = -1;
    private int _visibleLast = -1;
    private bool _smoothing;
    private DispatcherTimer? _smoothTimer;
    private bool _dragPressed;   // 已按下左键（还没超过起步阈值）
    private bool _dragging;      // 正在拖拽
    private double _dragStartY;
    private double _dragStartX;
    private double _dragStartOffset;
    private double _dragStartHorizontalOffset;

    public event Action<int>? CurrentPageChanged;
    public event Action? LayoutReady;
    public event Action<double>? ScaleChanged;

    public double ScaleFactor
    {
        get => _scale;
        set
        {
            double v = Math.Clamp(value, 0.2, 3.0);
            if (Math.Abs(v - _scale) < 0.001) return;
            double fraction = ScrollFraction;
            _scale = v;
            Rebuild();
            ScrollToFraction(fraction);
            ScaleChanged?.Invoke(v);
        }
    }

    public double ScrollFraction =>
        Scroll.ScrollableHeight <= 0 ? 0 : Math.Clamp(Scroll.VerticalOffset / Scroll.ScrollableHeight, 0, 1);

    public int CurrentPage => _currentIndex;
    public bool IsReady => _layoutReady;
    public int PageCount => _dims.Count;
    internal double CanvasWidth => RootCanvas.Width;
    internal double CanvasHeight => RootCanvas.Height;
    internal int RenderedCount => _rendered.Count;
    internal int RenderedWithSourceCount => _rendered.Values.Count(img => img.Source is not null);
    internal int RenderedWithoutSourceCount => _rendered.Values.Count(img => img.Source is null);
    internal int VisibleFirst => _visibleFirst;
    internal int VisibleLast => _visibleLast;
    internal double ScrollOffset => Scroll.VerticalOffset;
    internal double HorizontalOffset => Scroll.HorizontalOffset;
    internal double HorizontalScrollableWidth => Scroll.ScrollableWidth;

    internal void TestScrollHorizontal(double offset) =>
        Scroll.ScrollToHorizontalOffset(Math.Clamp(offset, 0, Math.Max(0, Scroll.ScrollableWidth)));

    internal void EnsureLayout()
    {
        if (!_layoutReady || _dims.Count == 0) return;
        double fraction = ScrollFraction;
        Rebuild();
        ScrollToFraction(fraction);
        NotifyCurrent();
    }

    public WebtoonViewer()
    {
        InitializeComponent();
        Unloaded += (_, _) => EndDrag();
        IsVisibleChanged += (_, _) => { if (!IsVisible) EndDrag(); };
        LayoutUpdated += (_, _) =>
        {
            if (_layoutReady && _dims.Count > 0 && Math.Abs(Scroll.ViewportWidth - _lastViewportWidth) > 1)
            {
                _lastViewportWidth = Scroll.ViewportWidth;
                double fraction = ScrollFraction;
                Rebuild();
                ScrollToFraction(fraction);
            }
        };
    }

    public async Task InitializeAsync(ComicImageLoader loader, Func<int, (int W, int H)?> dimProvider, int startPage, double scale)
    {
        ShowLoading("正在排版条漫…");
        _loader = loader;
        _scale = Math.Clamp(scale, 0.2, 3.0);
        ScaleChanged?.Invoke(_scale);
        _lastViewportWidth = -1;
        _layoutReady = false;
        _dims.Clear();
        var dims = await Task.Run(() =>
        {
            var list = new List<(int W, int H)>(loader.PageCount);
            for (int i = 0; i < loader.PageCount; i++)
            {
                var d = dimProvider(i);
                list.Add(d ?? (800, 1200));
            }
            return list;
        }).ConfigureAwait(true);
        _dims.AddRange(dims);
        Rebuild();
        _lastViewportWidth = Scroll.ViewportWidth;
        _layoutReady = true;
        int start = _pendingIndex >= 0
            ? Math.Clamp(_pendingIndex, 0, Math.Max(0, _dims.Count - 1))
            : Math.Clamp(startPage, 0, Math.Max(0, _dims.Count - 1));
        _pendingIndex = -1;
        ScrollToPage(start);
        NotifyCurrent();
        HideLoading();
        LayoutReady?.Invoke();
    }

    internal void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    internal void HideLoading() => LoadingOverlay.Visibility = Visibility.Collapsed;

    // ---- 鼠标左键拖拽滑动（条漫）----

    internal (bool Ok, string Detail) TestRoutedClickRelease()
    {
        // 使用真实 WPF 路由与捕获目标，不能直接设置拖动字段，否则会漏掉 MouseUp 收不到的问题。
        RootCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        bool downSeen = _dragPressed;
        bool capturedOnClick = IsMouseCaptured;
        // 捕获在 UserControl 上时，MouseUp 路由从这里开始，不再经过子 ScrollViewer。
        // 即使 CI 桌面不允许实际捕获，也必须检查这条路由，不能退回画布掩盖缺陷。
        RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseUpEvent });
        bool released = !_dragPressed && !IsMouseCaptured;
        string detail = $"downSeen={downSeen}, clickCaptured={capturedOnClick}, released={released}";
        EndDrag(); // 失败时也释放捕获，避免让自检本身抢住鼠标。
        return (downSeen && !capturedOnClick && released, detail);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_layoutReady) return;
        // 点在滚动条（含滑块/空白轨道）上时交给 ScrollViewer 自己处理，不要抢，
        // 否则滚动条会拖不动，看起来像界面卡住
        if (IsFromScrollBar(e.OriginalSource as DependencyObject)) return;
        CancelSmoothScroll();
        _dragPressed = true;
        _dragging = false;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartX = e.GetPosition(this).X;
        _dragStartOffset = Scroll.VerticalOffset;
        _dragStartHorizontalOffset = Scroll.HorizontalOffset;
        // 单击不捕获鼠标；只有实际拖动时才捕获，避免普通点击影响其它控件。
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPressed) return;
        if (e.LeftButton != MouseButtonState.Pressed) // 在控件外松手等异常情况，直接收尾
        {
            EndDrag();
            return;
        }
        var position = e.GetPosition(this);
        double y = position.Y;
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStartX) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(y - _dragStartY) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!CaptureMouse())
            {
                EndDrag();
                return;
            }
            _dragging = true;
            Cursor = Cursors.SizeAll;   // 拖动中的光标反馈
        }
        CancelSmoothScroll();       // 抓住内容时停止滚轮遗留的滑动
        Scroll.ScrollToVerticalOffset(_dragStartOffset - (y - _dragStartY));
        if (Scroll.ScrollableWidth > 0.5)
            Scroll.ScrollToHorizontalOffset(_dragStartHorizontalOffset - (position.X - _dragStartX));
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragPressed) return;
        bool wasDragging = _dragging;
        EndDrag();
        if (wasDragging) e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // 子滚动条也会冒泡 LostMouseCapture，只处理本控件自己的捕获丢失。
        if (ReferenceEquals(e.OriginalSource, this)) EndDrag();
    }

    /// <summary>松手即停：不做惯性滑动，只有按住拖动时才移动内容。</summary>
    private void EndDrag()
    {
        _dragPressed = false;
        _dragging = false;
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private static bool IsFromScrollBar(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is System.Windows.Controls.Primitives.ScrollBar) return true;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    /// <summary>自检钩子：模拟按住左键向下拖拽 dy 像素后松手。</summary>
    internal void TestDragDown(double dy, int steps = 12)
    {
        if (!_layoutReady) return;
        CancelSmoothScroll();
        _dragPressed = true;
        _dragging = true;
        _dragStartY = 300;
        _dragStartX = 0;
        _dragStartOffset = Scroll.VerticalOffset;
        _dragStartHorizontalOffset = Scroll.HorizontalOffset;
        for (int i = 1; i <= steps; i++)
            Scroll.ScrollToVerticalOffset(_dragStartOffset - dy * i / steps);
        EndDrag();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ScaleFactor *= 1 + e.Delta / 1200.0;
            e.Handled = true;
            return;
        }

        // 平滑惯性滚动：目标位置累积，定时器以缓动方式逼近
        double current = double.IsNaN(_scrollTarget) ? Scroll.VerticalOffset : _scrollTarget;
        _scrollTarget = Math.Clamp(current - e.Delta * 2.5, 0, Math.Max(0, Scroll.ScrollableHeight));
        StartSmoothScroll();
        e.Handled = true;
    }

    private void StartSmoothScroll()
    {
        if (_smoothTimer is null)
        {
            _smoothTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _smoothTimer.Tick += SmoothTick;
        }
        if (!_smoothTimer.IsEnabled)
            _smoothTimer.Start();
    }

    private void SmoothTick(object? sender, EventArgs e)
    {
        if (double.IsNaN(_scrollTarget)) return;
        double current = Scroll.VerticalOffset;
        double diff = _scrollTarget - current;
        if (Math.Abs(diff) < 0.5)
        {
            _smoothing = true;
            Scroll.ScrollToVerticalOffset(_scrollTarget);
            _smoothing = false;
            _scrollTarget = double.NaN;
            _smoothTimer?.Stop();
            return;
        }
        _smoothing = true;
        Scroll.ScrollToVerticalOffset(current + diff * 0.35);
        _smoothing = false;
    }

    private void CancelSmoothScroll()
    {
        _scrollTarget = double.NaN;
        _smoothTimer?.Stop();
    }

    public void ScrollToPage(int index)
    {
        CancelSmoothScroll();
        if (!_layoutReady || _tops.Count == 0)
        {
            // 条漫尚未完成排版时先记下目标页，初始化完成后跳过去
            _pendingIndex = index;
            return;
        }
        _pendingIndex = -1;
        index = Math.Clamp(index, 0, _dims.Count - 1);
        double y = Math.Clamp(_tops[index], 0, Math.Max(0, _total - Scroll.ViewportHeight));
        Scroll.ScrollToVerticalOffset(y);
        NotifyCurrent();
        ScheduleResync();
    }

    public void ScrollToFraction(double fraction)
    {
        CancelSmoothScroll();
        if (!_layoutReady) return;
        Scroll.ScrollToVerticalOffset(Math.Clamp(fraction, 0, 1) * Math.Max(0, Scroll.ScrollableHeight));
        ScheduleResync();
    }

    public void ScrollBy(double dy)
    {
        CancelSmoothScroll();
        Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + dy);
    }

    private void Rebuild()
    {
        _buildVersion++;
        CancelAllLoads();
        _visibleFirst = -1;
        _visibleLast = -1;
        long version = _buildVersion;
        double s = _scale;
        double canvasW = Math.Max(1, Scroll.ViewportWidth) * s;
        _tops.Clear();
        double y = 0;
        foreach (var d in _dims)
        {
            _tops.Add(y);
            y += d.Height * (canvasW / Math.Max(1, d.Width));
        }
        _total = y;
        RootCanvas.Width = canvasW;
        RootCanvas.Height = Math.Max(1, _total);

        foreach (var img in _rendered.Values)
            RootCanvas.Children.Remove(img);
        _rendered.Clear();
        UpdateVisible(version);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_smoothing)
            _scrollTarget = double.NaN; // 用户直接拖动/键盘操作时取消平滑
        if (!_layoutReady) return;
        UpdateVisible(_buildVersion);
        NotifyCurrent();
    }

    private void UpdateVisible(long version)
    {
        if (version != _buildVersion || _loader is null) return;
        double top = Scroll.VerticalOffset - Scroll.ViewportHeight;
        double bottom = Scroll.VerticalOffset + Scroll.ViewportHeight * 2;
        int first = Math.Max(0, BinarySearch(_tops, top));
        int last = Math.Min(_dims.Count - 1, BinarySearch(_tops, bottom));
        _visibleFirst = first;
        _visibleLast = last;

        // 只取消离开视口的页的加载，避免误伤仍然可见、正在加载的页
        foreach (var kv in _rendered.Where(kv => kv.Key < first || kv.Key > last).ToList())
        {
            RootCanvas.Children.Remove(kv.Value);
            _rendered.Remove(kv.Key);
            CancelLoad(kv.Key);
        }

        for (int i = first; i <= last; i++)
        {
            if (_rendered.TryGetValue(i, out var existing))
            {
                // 页仍在视口内但没出图（加载曾被打断），补一次加载
                if (existing.Source is null && !_loadCts.ContainsKey(i))
                    StartLoad(i, existing, version);
                continue;
            }
            double imageTop = Math.Floor(_tops[i]);
            double imageBottom = i + 1 < _tops.Count ? Math.Floor(_tops[i + 1]) : Math.Ceiling(_total);
            var img = new Image
            {
                Width = RootCanvas.Width,
                // 位置和高度都按整像素排布，并让相邻页重叠 1 DIP，避免高分屏缩放后露出背景细缝。
                Height = Math.Max(1, imageBottom - imageTop + 1),
                Stretch = System.Windows.Media.Stretch.Fill
            };
            System.Windows.Media.RenderOptions.SetEdgeMode(img, System.Windows.Media.EdgeMode.Aliased);
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
            Canvas.SetTop(img, imageTop);
            RootCanvas.Children.Add(img);
            _rendered[i] = img;
            StartLoad(i, img, version);
        }
    }

    private void StartLoad(int index, Image img, long version)
    {
        var cts = new CancellationTokenSource();
        _loadCts[index] = cts;
        _ = LoadAsync(index, img, version, cts);
    }

    private void CancelLoad(int index)
    {
        if (_loadCts.Remove(index, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelAllLoads()
    {
        foreach (var cts in _loadCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _loadCts.Clear();
    }

    /// <summary>大画布下滚动偏移是延迟生效的：跳页后持续校正几次，确保可见区域真的加载出来。</summary>
    private void ScheduleResync()
    {
        _resyncTicks = 8;
        _resyncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resyncTimer.Tick -= ResyncTick;
        _resyncTimer.Tick += ResyncTick;
        _resyncTimer.Stop();
        _resyncTimer.Start();
    }

    private void ResyncTick(object? sender, EventArgs e)
    {
        if (--_resyncTicks <= 0) _resyncTimer?.Stop();
        if (!_layoutReady || _loader is null) return;
        UpdateVisible(_buildVersion);
        NotifyCurrent();
    }

    private async Task LoadAsync(int index, Image img, long version, CancellationTokenSource cts)
    {
        if (_loader is null) return;
        var ct = cts.Token;
        try
        {
            var bmp = await _loader.GetPageAsync(index, ct).ConfigureAwait(true);
            if (version != _buildVersion || ct.IsCancellationRequested) return;
            if (bmp is null && !ct.IsCancellationRequested)
            {
                // 瞬时解码失败重试一次，避免条漫出现整条白屏
                await Task.Delay(150, ct);
                if (version != _buildVersion || ct.IsCancellationRequested) return;
                bmp = await _loader.GetPageAsync(index, ct).ConfigureAwait(true);
                if (version != _buildVersion || ct.IsCancellationRequested) return;
            }
            img.Source = bmp;
        }
        catch (OperationCanceledException)
        {
            // 页已离开视口或布局已重建
        }
        finally
        {
            if (_loadCts.TryGetValue(index, out var cur) && ReferenceEquals(cur, cts))
                _loadCts.Remove(index);
            cts.Dispose();
        }
    }

    private void NotifyCurrent()
    {
        if (!_layoutReady || _tops.Count == 0) return;
        int idx = Math.Clamp(BinarySearch(_tops, Scroll.VerticalOffset + 40), 0, _dims.Count - 1);
        if (idx != _currentIndex)
        {
            _currentIndex = idx;
            CurrentPageChanged?.Invoke(idx);
        }
    }

    private static int BinarySearch(List<double> tops, double value)
    {
        int lo = 0, hi = tops.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (tops[mid] <= value)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }
}
