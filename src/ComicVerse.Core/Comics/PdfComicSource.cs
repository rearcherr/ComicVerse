using System.IO;

namespace ComicVerse.Core.Comics;

/// <summary>
/// PDF 漫画源：常规页面整页渲染；超长页（条漫，高度/宽度超过阈值）按段切片，
/// 每段作为独立一页，避免超出位图尺寸上限（如 560×132842pt 的单页长条漫）。
/// </summary>
public sealed class PdfComicSource : IComicSource
{
    private const int RenderWidthPx = 1600;
    private const float MaxSliceAspect = 2.2f;
    private const int MaxSliceHeightPx = 4096;

    private readonly PdfNativeRenderer _renderer;
    private readonly List<PdfSlice> _slices = new();

    public string SourcePath { get; }
    public int PageCount => _slices.Count;

    public PdfComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _renderer = new PdfNativeRenderer(path);
            for (int page = 0; page < _renderer.PageCount; page++)
            {
                var (pw, ph) = _renderer.GetPageSize(page);
                if (pw <= 0 || ph <= 0) continue;

                float aspect = ph / pw;
                int sliceCount = aspect > MaxSliceAspect ? (int)Math.Ceiling(aspect / MaxSliceAspect) : 1;
                float sliceHeight = ph / sliceCount;
                for (int s = 0; s < sliceCount; s++)
                {
                    _slices.Add(new PdfSlice(page, s * sliceHeight, (s + 1) * sliceHeight, pw, ph));
                }
            }
            if (_slices.Count == 0)
                throw new ComicSourceException("PDF 页面尺寸无效");
        }
        catch (ComicSourceException)
        {
            _renderer?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _renderer?.Dispose();
            throw new ComicSourceException("无法打开 PDF 文件: " + ex.Message, ex);
        }
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _slices.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var slice = _slices[index];
        int heightPx = Math.Clamp(
            (int)Math.Round((slice.Y1 - slice.Y0) / slice.PageWidth * RenderWidthPx),
            64, MaxSliceHeightPx);
        return new MemoryStream(_renderer.RenderPng(slice.Page, slice.Y0, slice.Y1, RenderWidthPx, heightPx));
    }

    public (int Width, int Height)? GetPageSize(int index)
    {
        if (index < 0 || index >= _slices.Count) return null;
        var slice = _slices[index];
        int heightPx = Math.Clamp(
            (int)Math.Round((slice.Y1 - slice.Y0) / slice.PageWidth * RenderWidthPx),
            64, MaxSliceHeightPx);
        return (RenderWidthPx, heightPx);
    }

    public void Dispose() => _renderer?.Dispose();

    private readonly record struct PdfSlice(int Page, float Y0, float Y1, float PageWidth, float PageHeight);
}
