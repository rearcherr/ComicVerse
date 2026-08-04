using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _smoothing;
    private DispatcherTimer? _smoothTimer;

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
        ScrollToPage(Math.Clamp(startPage, 0, Math.Max(0, _dims.Count - 1)));
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
        if (!_layoutReady || _tops.Count == 0) return;
        index = Math.Clamp(index, 0, _dims.Count - 1);
        double y = Math.Clamp(_tops[index], 0, Math.Max(0, _total - Scroll.ViewportHeight));
        Scroll.ScrollToVerticalOffset(y);
        NotifyCurrent();
    }

    public void ScrollToFraction(double fraction)
    {
        CancelSmoothScroll();
        if (!_layoutReady) return;
        Scroll.ScrollToVerticalOffset(Math.Clamp(fraction, 0, 1) * Math.Max(0, Scroll.ScrollableHeight));
    }

    public void ScrollBy(double dy)
    {
        CancelSmoothScroll();
        Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + dy);
    }

    private void Rebuild()
    {
        _buildVersion++;
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

        foreach (var kv in _rendered.Where(kv => kv.Key < first || kv.Key > last).ToList())
        {
            RootCanvas.Children.Remove(kv.Value);
            _rendered.Remove(kv.Key);
        }

        for (int i = first; i <= last; i++)
        {
            if (_rendered.ContainsKey(i)) continue;
            var img = new Image
            {
                Width = RootCanvas.Width,
                Height = _dims[i].Height * (RootCanvas.Width / Math.Max(1, _dims[i].Width)),
                Stretch = System.Windows.Media.Stretch.Fill
            };
            Canvas.SetTop(img, _tops[i]);
            RootCanvas.Children.Add(img);
            _rendered[i] = img;
            int idx = i;
            _ = LoadAsync(idx, img, version);
        }
    }

    private async Task LoadAsync(int index, Image img, long version)
    {
        if (_loader is null) return;
        var bmp = await _loader.GetPageAsync(index).ConfigureAwait(true);
        if (version != _buildVersion) return;
        img.Source = bmp;
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
