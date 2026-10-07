using System.IO;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace ComicVerse.Core.Comics;

/// <summary>
/// PDF 漫画源（安卓）：基于系统 PdfRenderer 整页渲染；超长页（条漫）按段切片，
/// 每段作为独立一页，避免超出位图尺寸上限（与桌面端同一套切片规则）。
/// </summary>
public sealed class PdfComicSource : IComicSource
{
    private const int RenderWidthPx = 1600;
    private const float MaxSliceAspect = 2.2f;
    private const int MaxSliceHeightPx = 4096;

    private ParcelFileDescriptor? _pfd;
    private PdfRenderer? _renderer;
    private readonly List<PdfSlice> _slices = new();
    private readonly object _gate = new();
    private volatile bool _disposed;

    public string SourcePath { get; }
    public int PageCount => _slices.Count;

    public PdfComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _pfd = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly);
            if (_pfd is null)
                throw new ComicSourceException("无法打开 PDF 文件");
            _renderer = new PdfRenderer(_pfd);
            if (_renderer is null)
                throw new ComicSourceException("无法创建 PDF 渲染器");

            for (int page = 0; page < _renderer.PageCount; page++)
            {
                using var p = _renderer.OpenPage(page);
                float pw = p.Width;
                float ph = p.Height;
                if (pw <= 0 || ph <= 0) continue;

                float aspect = ph / pw;
                int sliceCount = aspect > MaxSliceAspect ? (int)Math.Ceiling(aspect / MaxSliceAspect) : 1;
                float sliceHeight = ph / sliceCount;
                for (int s = 0; s < sliceCount; s++)
                    _slices.Add(new PdfSlice(page, s * sliceHeight, (s + 1) * sliceHeight, pw));
            }
            if (_slices.Count == 0)
                throw new ComicSourceException("PDF 页面尺寸无效");
        }
        catch (ComicSourceException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Dispose();
            throw new ComicSourceException("无法打开 PDF 文件: " + ex.Message, ex);
        }
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _slices.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var slice = _slices[index];
        int heightPx = SliceHeightPx(slice);
        using var bmp = RenderSlice(slice, heightPx);
        return new MemoryStream(ImageHelper.EncodePng(bmp));
    }

    public (int Width, int Height)? GetPageSize(int index)
    {
        if (index < 0 || index >= _slices.Count) return null;
        var slice = _slices[index];
        return (RenderWidthPx, SliceHeightPx(slice));
    }

    private static int SliceHeightPx(PdfSlice slice) => Math.Clamp(
        (int)Math.Round((slice.Y1 - slice.Y0) / slice.PageWidth * RenderWidthPx),
        64, MaxSliceHeightPx);

    private Bitmap RenderSlice(PdfSlice slice, int heightPx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // PdfRenderer 不允许并发打开/渲染页面，串行化
        lock (_gate)
        {
            using var page = _renderer!.OpenPage(slice.Page);
            var bmp = Bitmap.CreateBitmap(RenderWidthPx, heightPx, Bitmap.Config.Argb8888!)
                      ?? throw new ComicSourceException("PDF 页面渲染失败");
            try
            {
                using var canvas = new Canvas(bmp);
                canvas.DrawColor(Color.White);
                using var matrix = new Matrix();
                float scale = RenderWidthPx / (float)page.Width;
                matrix.SetScale(scale, scale);
                matrix.PostTranslate(0, -slice.Y0 * scale);
                page.Render(bmp, null, matrix, PdfRenderMode.ForDisplay);
                return bmp;
            }
            catch
            {
                bmp.Recycle();
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try { _renderer?.Close(); } catch { }
            _renderer?.Dispose();
            _renderer = null;
            try { _pfd?.Close(); } catch { }
            _pfd?.Dispose();
            _pfd = null;
        }
    }

    private readonly record struct PdfSlice(int Page, float Y0, float Y1, float PageWidth);
}
