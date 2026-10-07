using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace ComicVerse.Droid.Ui;

/// <summary>
/// 翻页 / 双页阅读画布：单指拖动平移、双指缩放、双击缩放、左右点击翻页。
/// 内容尺寸既可以是单页，也可以是左右两页（双页模式）。
/// </summary>
public class ComicPagerView : View
{
    private readonly GestureDetector _gestures;
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly Paint _paint = new(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);

    private Bitmap? _left;
    private Bitmap? _right;
    private string _fitMode = "width";
    private double _customZoom = 1.0;
    private float _scale = 1f;
    private float _tx;
    private float _ty;
    private float _contentW;
    private float _contentH;
    private float _baseWidthScale = 1f;
    private float _alpha = 1f;
    private float _lastFocalX;
    private float _lastFocalY;

    public event Action? LeftZoneTapped;
    public event Action? RightZoneTapped;
    public event Action? CenterTapped;
    public event Action<double>? ZoomChanged;
    public event Action? PanStarted;

    public double ZoomPercent => _baseWidthScale > 0 ? _scale / _baseWidthScale * 100.0 : 100.0;
    public string FitMode => _fitMode;

    public ComicPagerView(Context context) : base(context)
    {
        _gestures = new GestureDetector(context, new GestureListener(this));
        _scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
        SetBackgroundColor(AppTheme.ReaderBg);
        Focusable = true;
        Clickable = true;
    }

    public void SetPages(Bitmap? left, Bitmap? right)
    {
        _left = left;
        _right = right;
        MeasureContent();
        ApplyScale(keepCenter: true);
        FadeIn();
        Invalidate();
    }

    public void SetFitMode(string mode, double customZoom = 1.0)
    {
        _fitMode = mode;
        if (mode == "custom") _customZoom = Math.Clamp(customZoom, 0.2, 6.0);
        ApplyScale(keepCenter: true);
        Invalidate();
    }

    /// <summary>相对「适应宽度」的缩放倍率（1.0 = 适应宽度）。</summary>
    public void SetZoomRelative(double factor, bool clampOnly = false)
    {
        _customZoom = Math.Clamp(factor, 0.2, 6.0);
        _fitMode = "custom";
        ApplyScale(keepCenter: true);
        Invalidate();
        ZoomChanged?.Invoke(ZoomPercent / 100.0);
    }

    private void MeasureContent()
    {
        float gap = _right is not null && _left is not null ? UiKit.Dp(10) : 0;
        if (_left is not null && _right is not null)
        {
            _contentW = _left.Width + gap + _right.Width;
            _contentH = Math.Max(_left.Height, _right.Height);
        }
        else if (_left is not null)
        {
            _contentW = _left.Width;
            _contentH = _left.Height;
        }
        else if (_right is not null)
        {
            _contentW = _right.Width;
            _contentH = _right.Height;
        }
        else
        {
            _contentW = 1;
            _contentH = 1;
        }
    }

    private void ApplyScale(bool keepCenter)
    {
        if (Width <= 0 || Height <= 0 || _contentW <= 0) return;
        float widthScale = Width / _contentW;
        float heightScale = Height / _contentH;
        _baseWidthScale = widthScale;
        _scale = _fitMode switch
        {
            "height" => heightScale,
            "page" => Math.Min(widthScale, heightScale),
            "actual" => 1f,
            "custom" => (float)(widthScale * _customZoom),
            _ => widthScale
        };
        _scale = Math.Clamp(_scale, 0.02f, 12f);
        ClampTranslate(keepCenter);
    }

    private void ClampTranslate(bool keepCenter)
    {
        float scaledW = _contentW * _scale;
        float scaledH = _contentH * _scale;
        if (keepCenter && (Math.Abs(_tx) < 0.5f && Math.Abs(_ty) < 0.5f))
        {
            _tx = (Width - scaledW) / 2f;
            _ty = (Height - scaledH) / 2f;
            return;
        }
        if (scaledW <= Width) _tx = (Width - scaledW) / 2f;
        else _tx = Math.Clamp(_tx, Width - scaledW, 0);
        if (scaledH <= Height) _ty = (Height - scaledH) / 2f;
        else _ty = Math.Clamp(_ty, Height - scaledH, 0);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        MeasureContent();
        ApplyScale(keepCenter: true);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_contentW <= 1f) return;
        canvas.Save();
        canvas.Translate(_tx, _ty);
        canvas.Scale(_scale, _scale);
        _paint.Alpha = (int)Math.Clamp(_alpha * 255f, 0, 255);
        if (_left is not null) canvas.DrawBitmap(_left, 0, 0, _paint);
        if (_right is not null && _left is not null)
            canvas.DrawBitmap(_right, _left.Width + UiKit.Dp(10), 0, _paint);
        else if (_right is not null)
            canvas.DrawBitmap(_right, 0, 0, _paint);
        canvas.Restore();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _scaleDetector.OnTouchEvent(e);
        if (!_scaleDetector.IsInProgress)
            _gestures.OnTouchEvent(e);

        if (e.ActionMasked == MotionEventActions.Down) PanStarted?.Invoke();
        return true;
    }

    private void FadeIn()
    {
        _alpha = 0.25f;
        var animator = ValueAnimator.OfFloat(0.25f, 1f);
        animator!.SetDuration(180);
        animator.AddUpdateListener(new UpdateListener(value =>
        {
            _alpha = value;
            Invalidate();
        }));
        animator.Start();
    }

    private sealed class UpdateListener : Java.Lang.Object, ValueAnimator.IAnimatorUpdateListener
    {
        private readonly Action<float> _onUpdate;
        public UpdateListener(Action<float> onUpdate) => _onUpdate = onUpdate;
        public void OnAnimationUpdate(ValueAnimator? animation)
        {
            float value = (animation?.AnimatedValue as Java.Lang.Float)?.FloatValue() ?? 1f;
            _onUpdate(value);
        }
    }

    private sealed class GestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly ComicPagerView _owner;
        public GestureListener(ComicPagerView owner) => _owner = owner;

        public override bool OnDown(MotionEvent? e) => true;

        public override bool OnSingleTapConfirmed(MotionEvent? e)
        {
            float x = e?.GetX() ?? 0;
            float width = Math.Max(1, _owner.Width);
            if (x < width * 0.34f) _owner.LeftZoneTapped?.Invoke();
            else if (x > width * 0.66f) _owner.RightZoneTapped?.Invoke();
            else _owner.CenterTapped?.Invoke();
            return true;
        }

        public override bool OnDoubleTap(MotionEvent? e)
        {
            double current = _owner.ZoomPercent / 100.0;
            _owner.SetZoomRelative(current > 1.35 ? 1.0 : 2.5);
            return true;
        }

        public override bool OnScroll(MotionEvent? e1, MotionEvent? e2, float distanceX, float distanceY)
        {
            if (_owner._contentW * _owner._scale <= _owner.Width) distanceX = 0;
            if (_owner._contentH * _owner._scale <= _owner.Height) distanceY = 0;
            _owner._tx -= distanceX;
            _owner._ty -= distanceY;
            _owner.ClampTranslate(false);
            _owner.Invalidate();
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent? e2, float velocityX, float velocityY)
        {
            return true;
        }
    }

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly ComicPagerView _owner;
        public ScaleListener(ComicPagerView owner) => _owner = owner;

        public override bool OnScale(ScaleGestureDetector? detector)
        {
            if (detector is null) return false;
            float factor = detector.ScaleFactor;
            float newScale = Math.Clamp(_owner._scale * factor, 0.02f, 12f);
            float applied = newScale / _owner._scale;

            float focusX = detector.FocusX;
            float focusY = detector.FocusY;
            _owner._tx = focusX - (focusX - _owner._tx) * applied;
            _owner._ty = focusY - (focusY - _owner._ty) * applied;
            _owner._scale = newScale;
            _owner._customZoom = Math.Clamp(_owner.ZoomPercent / 100.0, 0.2, 6.0);
            _owner._fitMode = "custom";
            _owner.ClampTranslate(false);
            _owner.Invalidate();
            _owner.ZoomChanged?.Invoke(_owner.ZoomPercent / 100.0);
            return true;
        }
    }
}
