using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PdfiumViewer;

namespace ComicVerse.Core.Comics;

/// <summary>PDF 漫画源：按页渲染为位图（Pdfium），只渲染当前需要的页。</summary>
public sealed class PdfComicSource : IComicSource
{
    private readonly PdfDocument _doc;

    public string SourcePath { get; }
    public int PageCount => _doc.PageCount;

    public PdfComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _doc = PdfDocument.Load(path);
            if (_doc.PageCount == 0)
                throw new ComicSourceException("PDF 没有页面");
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
        if (index < 0 || index >= _doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        var size = _doc.PageSizes[index];
        const double targetWidth = 1600.0;
        double scale = Math.Clamp(targetWidth / Math.Max(1.0, size.Width), 0.5, 3.0);
        int w = Math.Max(64, (int)(size.Width * scale));
        int h = Math.Max(64, (int)(size.Height * scale));

        using var bmp = _doc.Render(index, w, h, 96, 96, PdfRotation.Rotate0, PdfRenderFlags.Annotations);
        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _doc?.Dispose();
    }
}
