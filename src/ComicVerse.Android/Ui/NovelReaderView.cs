using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Novel;
using OperationCanceledException = System.OperationCanceledException;

namespace ComicVerse.Droid.Ui;

/// <summary>
/// 小说阅读器：翻页（按行分页）与滚动两种方式，排版参数与桌面端一致。
/// </summary>
public class NovelReaderView : View
{
    private readonly TextPaint _textPaint = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    private readonly Paint _backgroundPaint = new();
    private StaticLayout? _layout;
    private readonly List<int> _pageStarts = new();
    private int _pageIndex;
    private float _scrollY;
    private string _subMode = "paged";
    private NovelChapter? _chapter;
    private Func<string, Stream>? _imageResolver;
    private NovelViewSettings _settings = new();
    private float _lastTouchY;
    private bool _dragging;
    private readonly OverScroller _scroller;
    private readonly VelocityTracker _velocity;
    private bool _autoScroll;
    private int _autoSpeed = 60;
    private readonly Handler _autoHandler = new(Android.OS.Looper.MainLooper!);
    private readonly Java.Lang.Runnable _autoRunnable;
    private readonly Dictionary<string, Bitmap?> _imageCache = new();

    public event Action? LeftZoneTapped;
    public event Action? RightZoneTapped;
    public event Action? CenterTapped;
    public event Action<int, int>? PageChanged;
    public event Action? NextChapterRequested;
    public event Action? PrevChapterRequested;
    public event Action? UserScrolled;
    public event Action<int>? ChapterRequested;

    public string SubMode => _subMode;
    public int PageIndex => _pageIndex;
    public int PageCount => Math.Max(1, _pageStarts.Count);
    public float ScrollFraction
    {
        get
        {
            float max = Math.Max(0, TotalHeight - Height);
            return max <= 0 ? 0 : Math.Clamp(_scrollY / max, 0, 1);
        }
    }
    private float TotalHeight => _layout is null ? 0 : _layout.Height + UiKit.Dp(2 * _settings.PageMargin / 3.0);

    public NovelReaderView(Context context) : base(context)
    {
        _scroller = new OverScroller(context);
        _velocity = VelocityTracker.Obtain()!;
        _autoRunnable = new Java.Lang.Runnable(AutoTick);
        SetBackgroundColor(AppTheme.ReaderBg);
        Clickable = true;
    }

    public void ApplySettings(NovelViewSettings settings, bool rebuild)
    {
        _settings = settings;
        _backgroundPaint.Color = UiKit.Argb(settings.Background);
        _textPaint.Color = UiKit.Argb(settings.TextColor);
        _textPaint.TextSize = UiKit.Dp(settings.FontSize);
        _textPaint.SetTypeface(Typeface.Create(ResolveFont(settings.FontFamily), TypefaceStyle.Normal));
        SetBackgroundColor(UiKit.Argb(settings.Background));
        if (rebuild && _chapter is not null) BuildLayout(keepPosition: true);
        Invalidate();
    }

    private static string ResolveFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return "sans-serif";
        string lower = family.ToLowerInvariant();
        if (lower.Contains("serif") && !lower.Contains("sans")) return "serif";
        if (lower.Contains("mono")) return "monospace";
        return "sans-serif";
    }

    public void SetSubMode(string mode, bool keepPosition = true)
    {
        _subMode = mode == "scroll" ? "scroll" : "paged";
        StopAutoScroll();
        if (_chapter is not null) BuildLayout(keepPosition);
        Invalidate();
    }

    public void Load(NovelChapter chapter, Func<string, Stream>? imageResolver, NovelViewSettings settings)
    {
        _chapter = chapter;
        _imageResolver = imageResolver;
        _settings = settings;
        _imageCache.Clear();
        BuildLayout(keepPosition: false);
    }

    private void BuildLayout(bool keepPosition)
    {
        float fraction = keepPosition ? (_subMode == "paged" ? (float)_pageIndex / Math.Max(1, PageCount - 1) : ScrollFraction) : 0;
        int width = Math.Max(1, Width - UiKit.Dp(2 * _settings.PageMargin));
        _textPaint.Color = UiKit.Argb(_settings.TextColor);
        _textPaint.TextSize = UiKit.Dp(_settings.FontSize);
        _textPaint.SetTypeface(Typeface.Create(ResolveFont(_settings.FontFamily), TypefaceStyle.Normal));

        var text = BuildSpannable(width);
        var builder = StaticLayout.Builder.Obtain((Java.Lang.ICharSequence)text, 0, text.Length(), _textPaint, width);
        builder!.SetAlignment(Android.Text.Layout.Alignment.AlignNormal);
        builder.SetLineSpacing(0, (float)Math.Clamp(_settings.LineSpacing, 1.0, 3.0));
        builder.SetIncludePad(true);
        _layout = builder.Build();

        Paginate();
        if (keepPosition)
        {
            if (_subMode == "paged")
                _pageIndex = Math.Clamp((int)Math.Round(fraction * Math.Max(0, PageCount - 1)), 0, PageCount - 1);
            else
                _scrollY = fraction * Math.Max(0, TotalHeight - Height);
        }
        else
        {
            _pageIndex = 0;
            _scrollY = 0;
        }
        ClampScroll();
        NotifyPage();
        Invalidate();
    }

    private SpannableStringBuilder BuildSpannable(int width)
    {
        var builder = new SpannableStringBuilder();
        if (_chapter is null) return builder;
        bool first = true;
        int blankLines = (int)Math.Clamp(Math.Round(_settings.ParagraphSpacing / 12.0), 0, 2);
        string paragraphGap = "\n" + new string('\n', blankLines);
        foreach (var block in _chapter.Blocks)
        {
            if (block.IsImage && block.ImageEntry is not null)
            {
                var bitmap = ResolveImage(block.ImageEntry);
                if (bitmap is not null)
                {
                    int w = width;
                    int h = Math.Max(1, (int)(bitmap.Height * ((double)w / bitmap.Width)));
                    var drawable = new BitmapDrawable(Resources, bitmap);
                    drawable.SetBounds(0, 0, w, h);
                    int start = builder.Length();
                    builder.Append("\uFFFC");
                    builder.SetSpan(new ImageSpan(drawable), start, builder.Length(), SpanTypes.ExclusiveExclusive);
                    builder.Append("\n");
                }
                continue;
            }

            string text = block.Text ?? "";
            if (text.Length == 0) continue;

            if (!first) builder.Append(paragraphGap);
            if (block.IsHeading)
            {
                int start = builder.Length();
                text = text.Trim();
                builder.Append(text);
                builder.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, builder.Length(), SpanTypes.ExclusiveExclusive);
                builder.SetSpan(new RelativeSizeSpan(1.18f), start, builder.Length(), SpanTypes.ExclusiveExclusive);
                builder.Append("\n");
            }
            else
            {
                builder.Append(text);
                builder.Append("\n");
            }
            first = false;
        }
        return builder;
    }

    private Bitmap? ResolveImage(string entry)
    {
        if (_imageCache.TryGetValue(entry, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            if (_imageResolver is not null)
            {
                using var stream = _imageResolver(entry);
                bitmap = ImageHelper.Decode(stream);
                if (bitmap is not null && bitmap.Width > 900)
                {
                    int w = 900;
                    int h = Math.Max(1, (int)(bitmap.Height * (w / (double)bitmap.Width)));
                    bitmap = Bitmap.CreateScaledBitmap(bitmap, w, h, true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("EPUB 插图解码失败: " + entry, ex);
        }
        _imageCache[entry] = bitmap;
        return bitmap;
    }

    private void Paginate()
    {
        _pageStarts.Clear();
        if (_layout is null) return;
        float pageHeight = Math.Max(50, Height - UiKit.Dp(2 * _settings.PageMargin));
        int line = 0;
        int total = _layout.LineCount;
        _pageStarts.Add(0);
        while (line < total)
        {
            float top = _layout.GetLineTop(line);
            int end = line;
            while (end < total && _layout.GetLineBottom(end) - top <= pageHeight) end++;
            if (end == line) end = line + 1;
            line = end;
            if (line < total) _pageStarts.Add(line);
        }
        if (_pageStarts.Count == 0) _pageStarts.Add(0);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (_chapter is not null) BuildLayout(keepPosition: true);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawRect(0, 0, Width, Height, _backgroundPaint);
        if (_layout is null) return;
        float margin = UiKit.Dp(_settings.PageMargin);
        canvas.Save();
        if (_subMode == "paged")
        {
            int startLine = _pageStarts[Math.Clamp(_pageIndex, 0, _pageStarts.Count - 1)];
            canvas.Translate(margin, margin - _layout.GetLineTop(startLine));
        }
        else
        {
            canvas.Translate(margin, margin - _scrollY);
        }
        _layout.Draw(canvas);
        canvas.Restore();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        if (_subMode == "paged")
        {
            if (e.ActionMasked == MotionEventActions.Up && !_dragging)
            {
                float x = e.GetX();
                if (x < Width * 0.34f) LeftZoneTapped?.Invoke();
                else if (x > Width * 0.66f) RightZoneTapped?.Invoke();
                else CenterTapped?.Invoke();
            }
            if (e.ActionMasked == MotionEventActions.Down) _dragging = false;
            if (e.ActionMasked == MotionEventActions.Move)
            {
                float dy = Math.Abs(e.GetY() - _lastTouchY);
                if (dy > UiKit.Dp(12) && !_dragging) _dragging = true;
            }
            _lastTouchY = e.GetY();
            return true;
        }

        _velocity.AddMovement(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragging = true;
                _scroller.ForceFinished(true);
                StopAutoScroll();
                UserScrolled?.Invoke();
                _lastTouchY = e.GetY();
                break;
            case MotionEventActions.Move:
                if (!_dragging)
                {
                    _dragging = true;
                    _lastTouchY = e.GetY();
                    break;
                }
                _scrollY -= e.GetY() - _lastTouchY;
                _lastTouchY = e.GetY();
                ClampScroll();
                NotifyPage();
                Invalidate();
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragging = false;
                _velocity.ComputeCurrentVelocity(1000);
                float vy = _velocity.YVelocity;
                if (Math.Abs(vy) > 200)
                {
                    _scroller.Fling(0, (int)_scrollY, 0, (int)-vy, 0, 0, 0, Math.Max(0, (int)(TotalHeight - Height)));
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
        _scrollY = _scroller.CurrY;
        ClampScroll();
        NotifyPage();
        PostInvalidateOnAnimation();
    }

    private void ClampScroll()
    {
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, TotalHeight - Height));
    }

    public void NextPage()
    {
        if (_subMode == "scroll")
        {
            _scrollY += Height * 0.9f;
            ClampScroll();
            NotifyPage();
            Invalidate();
            return;
        }
        if (_pageIndex < PageCount - 1) SetPage(_pageIndex + 1);
        else NextChapterRequested?.Invoke();
    }

    public void PrevPage()
    {
        if (_subMode == "scroll")
        {
            _scrollY -= Height * 0.9f;
            ClampScroll();
            NotifyPage();
            Invalidate();
            return;
        }
        if (_pageIndex > 0) SetPage(_pageIndex - 1);
        else PrevChapterRequested?.Invoke();
    }

    public void SetPage(int index)
    {
        _pageIndex = Math.Clamp(index, 0, PageCount - 1);
        NotifyPage();
        Invalidate();
    }

    public void SetScrollFraction(double fraction)
    {
        _scrollY = (float)(Math.Clamp(fraction, 0, 1) * Math.Max(0, TotalHeight - Height));
        ClampScroll();
        NotifyPage();
        Invalidate();
    }

    private void NotifyPage() => PageChanged?.Invoke(_pageIndex, PageCount);

    public void StartAutoScroll(int speed)
    {
        _autoSpeed = Math.Clamp(speed, 20, 160);
        if (_autoScroll) return;
        _autoScroll = true;
        _autoHandler.PostDelayed(_autoRunnable, 80);
    }

    public void StopAutoScroll()
    {
        if (!_autoScroll) return;
        _autoScroll = false;
        _autoHandler.RemoveCallbacks(_autoRunnable);
    }

    public bool AutoScrolling => _autoScroll;

    private void AutoTick()
    {
        if (!_autoScroll) return;
        _scrollY += _autoSpeed * 0.08f;
        float max = Math.Max(0, TotalHeight - Height);
        if (_scrollY >= max)
        {
            _scrollY = max;
            StopAutoScroll();
            NextChapterRequested?.Invoke();
        }
        NotifyPage();
        Invalidate();
        if (_autoScroll) _autoHandler.PostDelayed(_autoRunnable, 80);
    }

    public void Release()
    {
        StopAutoScroll();
        _velocity.Recycle();
    }
}
