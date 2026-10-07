using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using ComicVerse.Core.Services;

namespace ComicVerse.Droid.Ui;

/// <summary>
/// 条漫阅读器：按视口范围懒加载/卸载图片，支持惯性滚动、双指缩放与页号追踪。
/// </summary>
public class WebtoonReaderView : View
{
    private ComicImageLoader? _loader;
    private readonly List<(int W, int H)> _dims = new();
    private readonly List<float> _tops = new();
    private readonly Dictionary<int, Bitmap> _rendered = new();
    private readonly Dictionary<int, CancellationTokenSource> _loads = new();
    private readonly Paint _paint = new(PaintFlags.FilterBitmap);
    private readonly Paint _placeholder = new();
    private readonly OverScroller _scroller;
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly VelocityTracker _velocity;

    private float _scale = 1f;
    private float _canvasWidth;
    private float _total;
    private float _scrollX;
    private float _scrollY;
    private float _lastTouchX;
    private float _lastTouchY;
    private bool _dragging;
    private int _current = -1;
    private bool _layoutReady;
    private long _buildVersion;
    private int _pendingIndex = -1;
    private float _pinchAnchorFraction;
    private int _pinchAnchorPage;

    public event Action<int>? CurrentPageChanged;
    public event Action<double>? ScaleChanged;
    public event Action? LayoutReady;
    public event Action? UserScrolled;
    public event Action? Tapped;

    private float _downY;
    private long _downTime;
    private float _maxMove;

    public int PageCount => _dims.Count;
    public bool IsReady => _layoutReady;
    public int CurrentPage => _current;
    public float ScrollFraction => _total <= 0 ? 0 : Math.Clamp(_scrollY / Math.Max(1f, _total - Height), 0, 1);
    public float ScaleFactor => _scale;

    public WebtoonReaderView(Context context) : base(context)
    {
        _scroller = new OverScroller(context);
        _velocity = VelocityTracker.Obtain()!;
        _scaleDetector = new ScaleGestureDetector(context, new PinchListener(this));
        _placeholder.Color = AppTheme.SurfaceAlt;
        SetBackgroundColor(AppTheme.ReaderBg);
        Clickable = true;
    }

    public async Task InitializeAsync(ComicImageLoader loader, Func<int, (int W, int H)?> dimProvider,
        int startPage, float scale)
    {
        _loader = loader;
        _scale = Math.Clamp(scale, 0.2f, 3f);
        _layoutReady = false;
        _dims.Clear();
        _rendered.Clear();
        CancelAllLoads();

        var dims = await Task.Run(() =>
        {
            var list = new List<(int W, int H)>(loader.PageCount);
            for (int i = 0; i < loader.PageCount; i++)
                list.Add(dimProvider(i) ?? (800, 1200));
            return list;
        });

        _dims.AddRange(dims);
        Rebuild();
        _layoutReady = true;
        int start = _pendingIndex >= 0 ? _pendingIndex : startPage;
        _pendingIndex = -1;
        ScrollToPage(start);
        LayoutReady?.Invoke();
        Invalidate();
    }

    private void Rebuild()
    {
        _buildVersion++;
        CancelAllLoads();
        _rendered.Clear();

        _canvasWidth = Math.Max(1f, Width) * _scale;
        _tops.Clear();
        float y = 0;
        foreach (var dim in _dims)
        {
            _tops.Add(y);
            y += dim.H * (_canvasWidth / Math.Max(1, dim.W));
        }
        _total = y;
        ClampScroll();
    }

    private void ClampScroll()
    {
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _total - Height));
        _scrollX = _canvasWidth <= Width ? 0 : Math.Clamp(_scrollX, 0, _canvasWidth - Width);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (_layoutReady) Rebuild();
    }

    public void SetScaleFactor(double scale)
    {
        float target = (float)Math.Clamp(scale, 0.2, 3.0);
        if (Math.Abs(target - _scale) < 0.001f) return;
        _pinchAnchorPage = _current >= 0 ? _current : 0;
        _pinchAnchorFraction = _total <= 0 ? 0 : _scrollY / Math.Max(1f, _total);
        _scale = target;
        Rebuild();
        _scrollY = _pinchAnchorFraction * Math.Max(0, _total - Height);
        ClampScroll();
        ScaleChanged?.Invoke(_scale);
        UpdateVisible(_buildVersion);
        NotifyCurrent();
        Invalidate();
    }

    public void ScrollToPage(int index)
    {
        if (!_layoutReady || _tops.Count == 0)
        {
            _pendingIndex = index;
            return;
        }
        _pendingIndex = -1;
        int clamped = Math.Clamp(index, 0, _dims.Count - 1);
        _scrollY = Math.Clamp(_tops[clamped], 0, Math.Max(0, _total - Height));
        ClampScroll();
        _scroller.ForceFinished(true);
        UpdateVisible(_buildVersion);
        NotifyCurrent();
        Invalidate();
    }

    public void ScrollToFraction(double fraction)
    {
        if (!_layoutReady) return;
        _scrollY = (float)(Math.Clamp(fraction, 0, 1) * Math.Max(0, _total - Height));
        ClampScroll();
        UpdateVisible(_buildVersion);
        NotifyCurrent();
        Invalidate();
    }

    public void ScrollBy(float dy)
    {
        _scrollY += dy;
        ClampScroll();
        UpdateVisible(_buildVersion);
        NotifyCurrent();
        Invalidate();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _scaleDetector.OnTouchEvent(e);

        _velocity.AddMovement(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragging = true;
                _scroller.ForceFinished(true);
                _lastTouchX = e.GetX();
                _lastTouchY = e.GetY();
                _downY = e.GetY();
                _downTime = Android.OS.SystemClock.ElapsedRealtime();
                _maxMove = 0;
                UserScrolled?.Invoke();
                break;
            case MotionEventActions.Move:
                if (_scaleDetector.IsInProgress) break;
                if (!_dragging)
                {
                    _dragging = true;
                    _lastTouchX = e.GetX();
                    _lastTouchY = e.GetY();
                    break;
                }
                _scrollY -= e.GetY() - _lastTouchY;
                _scrollX -= e.GetX() - _lastTouchX;
                _maxMove = Math.Max(_maxMove, Math.Abs(e.GetY() - _downY));
                _lastTouchX = e.GetX();
                _lastTouchY = e.GetY();
                ClampScroll();
                UpdateVisible(_buildVersion);
                NotifyCurrent();
                Invalidate();
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragging = false;
                if (e.ActionMasked == MotionEventActions.Up && _maxMove < UiKit.Dp(10) &&
                    Android.OS.SystemClock.ElapsedRealtime() - _downTime < 400)
                {
                    Tapped?.Invoke();
                }
                _velocity.ComputeCurrentVelocity(1000);
                float vy = _velocity.YVelocity;
                float vx = _velocity.XVelocity;
                if (Math.Abs(vy) > 200 || Math.Abs(vx) > 200)
                {
                    _scroller.Fling(
                        (int)_scrollX, (int)_scrollY,
                        (int)-vx, (int)-vy,
                        0, Math.Max(0, (int)(_canvasWidth - Width)),
                        0, Math.Max(0, (int)(_total - Height)));
                    Invalidate();
                }
                break;
        }
        return true;
    }

    public override void ComputeScroll()
    {
        base.ComputeScroll();
        if (!_scroller.ComputeScrollOffset()) return;
        _scrollX = _scroller.CurrX;
        _scrollY = _scroller.CurrY;
        ClampScroll();
        UpdateVisible(_buildVersion);
        NotifyCurrent();
        PostInvalidateOnAnimation();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!_layoutReady || _dims.Count == 0) return;

        canvas.Save();
        canvas.Translate(-_scrollX, -_scrollY);
        int first = Math.Max(0, BinarySearch(_tops, _scrollY - Height * 0.5f));
        int last = Math.Min(_dims.Count - 1, BinarySearch(_tops, _scrollY + Height * 1.5f));
        for (int i = first; i <= last; i++)
        {
            // 按整像素取整 + 向下多铺 1px：相邻页高度是小数，抗锯齿会在交界处透出背景形成细白线
            float top = MathF.Round(_tops[i]);
            float nextTop = i + 1 < _tops.Count ? MathF.Round(_tops[i + 1]) : MathF.Round(_total);
            float bottom = MathF.Max(top + 1f, nextTop) + 1f;
            var dst = new RectF(_scrollX, top, _scrollX + _canvasWidth, bottom);
            if (_rendered.TryGetValue(i, out var bmp) && bmp is not null)
                canvas.DrawBitmap(bmp, null, dst, _paint);
            else
                canvas.DrawRect(dst, _placeholder);
        }
        canvas.Restore();
    }

    private void UpdateVisible(long version)
    {
        if (version != _buildVersion || _loader is null || !_layoutReady) return;
        int first = Math.Max(0, BinarySearch(_tops, _scrollY - Height));
        int last = Math.Min(_dims.Count - 1, BinarySearch(_tops, _scrollY + Height * 2f));

        foreach (int key in _rendered.Keys.Where(k => k < first || k > last).ToList())
        {
            _rendered.Remove(key);
            CancelLoad(key);
        }

        for (int i = first; i <= last; i++)
        {
            if (_rendered.ContainsKey(i)) continue;
            _rendered[i] = null!;
            StartLoad(i, version);
        }
    }

    private void StartLoad(int index, long version)
    {
        var cts = new CancellationTokenSource();
        _loads[index] = cts;
        _ = LoadAsync(index, version, cts);
    }

    private void CancelLoad(int index)
    {
        if (_loads.Remove(index, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelAllLoads()
    {
        foreach (var cts in _loads.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _loads.Clear();
    }

    private async Task LoadAsync(int index, long version, CancellationTokenSource cts)
    {
        if (_loader is null) return;
        var ct = cts.Token;
        try
        {
            var bmp = await _loader.GetPageAsync(index, ct);
            if (version != _buildVersion || ct.IsCancellationRequested) return;
            if (bmp is null && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct);
                if (version != _buildVersion || ct.IsCancellationRequested) return;
                bmp = await _loader.GetPageAsync(index, ct);
                if (version != _buildVersion || ct.IsCancellationRequested) return;
            }
            if (bmp is not null) _rendered[index] = bmp;
            PostInvalidateOnAnimation();
        }
        catch (OperationCanceledException)
        {
            // 页已离开视口
        }
        finally
        {
            if (_loads.TryGetValue(index, out var cur) && ReferenceEquals(cur, cts))
                _loads.Remove(index);
            cts.Dispose();
        }
    }

    private void NotifyCurrent()
    {
        if (!_layoutReady || _tops.Count == 0) return;
        int index = Math.Clamp(BinarySearch(_tops, _scrollY + Height * 0.15f), 0, _dims.Count - 1);
        if (index != _current)
        {
            _current = index;
            CurrentPageChanged?.Invoke(index);
        }
    }

    private static int BinarySearch(List<float> tops, float value)
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

    public void Release()
    {
        CancelAllLoads();
        _rendered.Clear();
        _velocity.Recycle();
    }

    private sealed class PinchListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly WebtoonReaderView _owner;
        private float _startScale;

        public PinchListener(WebtoonReaderView owner) => _owner = owner;

        public override bool OnScaleBegin(ScaleGestureDetector? detector)
        {
            _startScale = _owner._scale;
            return true;
        }

        public override bool OnScale(ScaleGestureDetector? detector)
        {
            if (detector is null) return false;
            float target = Math.Clamp(_startScale * detector.ScaleFactor, 0.2f, 3f);
            _owner.SetScaleFactor(target);
            return true;
        }
    }
}
