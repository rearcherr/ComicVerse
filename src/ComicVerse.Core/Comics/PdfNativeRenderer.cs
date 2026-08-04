using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Core.Comics;

/// <summary>
/// 基于 PDFium 的轻量 PDF 渲染器（P/Invoke）。
/// 支持用矩阵只渲染页面的一部分，从而把超长条漫 PDF 切成多段。
/// </summary>
public sealed class PdfNativeRenderer : IDisposable
{
    private static readonly object LibraryLock = new();
    private static readonly object RenderSync = new();
    private static bool _libraryInitialized;

    private readonly IntPtr _doc;

    public int PageCount { get; }

    static PdfNativeRenderer()
    {
        // 单文件发布时 pdfium.dll 被嵌入，首次使用前自动释放并加入 DLL 搜索路径
        PdfNative.EnsureExtracted(Path.Combine(Path.GetTempPath(), "ComicVerse", "native"));
    }

    public PdfNativeRenderer(string path)
    {
        lock (RenderSync)
        {
            lock (LibraryLock)
            {
                if (!_libraryInitialized)
                {
                    FPDF_InitLibrary();
                    _libraryInitialized = true;
                }
            }
            try
            {
                byte[] pathUtf8 = Encoding.UTF8.GetBytes(path + '\0');
                _doc = FPDF_LoadDocument(pathUtf8, null);
                if (_doc == IntPtr.Zero)
                    throw new ComicSourceException("无法打开 PDF（文件可能已损坏或加密）");
                PageCount = FPDF_GetPageCount(_doc);
                if (PageCount <= 0)
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
    }

    public (float Width, float Height) GetPageSize(int page)
    {
        lock (RenderSync)
        {
            IntPtr pageHandle = LoadPage(page);
            try
            {
                double w = FPDF_GetPageWidthF(pageHandle);
                double h = FPDF_GetPageHeightF(pageHandle);
                return ((float)w, (float)h);
            }
            finally
            {
                FPDF_ClosePage(pageHandle);
            }
        }
    }

    /// <summary>渲染页面在 [y0, y1]（pt）范围内的图像，输出 PNG 字节。</summary>
    public byte[] RenderPng(int page, float y0Points, float y1Points, int widthPx, int heightPx)
    {
        // pdfium 渲染不是线程安全的：同一文档（及跨文档）的渲染必须串行
        lock (RenderSync)
        {
            IntPtr pageHandle = LoadPage(page);
            try
            {
                return RenderPngCore(pageHandle, page, y0Points, y1Points, widthPx, heightPx);
            }
            finally
            {
                FPDF_ClosePage(pageHandle);
            }
        }
    }

    private byte[] RenderPngCore(IntPtr pageHandle, int page, float y0Points, float y1Points, int widthPx, int heightPx)
    {
        IntPtr bitmap = FPDFBitmap_Create(widthPx, heightPx, 0); // 0 = BGRx 不透明
        if (bitmap == IntPtr.Zero)
            throw new ComicSourceException("创建渲染位图失败");
        try
        {
            FPDFBitmap_FillRect(bitmap, 0, 0, widthPx, heightPx, 0xFFFFFFFF);
            double pageW = FPDF_GetPageWidthF(pageHandle);
            double scale = widthPx / pageW;
            var matrix = new FS_MATRIX
            {
                a = (float)scale,
                b = 0,
                c = 0,
                d = (float)scale,
                e = 0,
                f = (float)(-y0Points * scale)
            };
            var clip = new FS_RECTF { left = 0, top = 0, right = widthPx, bottom = heightPx };
            FPDF_RenderPageBitmapWithMatrix(bitmap, pageHandle, ref matrix, ref clip);

            int stride = widthPx * 4;
            var buffer = FPDFBitmap_GetBuffer(bitmap);
            var data = new byte[stride * heightPx];
            Marshal.Copy(buffer, data, 0, data.Length);
            return EncodeBgrxToPng(data, widthPx, heightPx, stride);
        }
        catch (Exception ex)
        {
            throw new ComicSourceException($"渲染第 {page + 1} 页失败: {ex.Message}", ex);
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    private IntPtr LoadPage(int page)
    {
        IntPtr p = FPDF_LoadPage(_doc, page);
        if (p == IntPtr.Zero)
            throw new ComicSourceException($"无法加载第 {page + 1} 页");
        return p;
    }

    private static byte[] EncodeBgrxToPng(byte[] bgrx, int width, int height, int stride)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgrx, stride);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (RenderSync)
        {
            if (_doc != IntPtr.Zero)
            {
                FPDF_CloseDocument(_doc);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FS_MATRIX
    {
        public float a, b, c, d, e, f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FS_RECTF
    {
        public float left, top, right, bottom;
    }

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadDocument(byte[] filePathUtf8, byte[]? password);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr doc);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(IntPtr doc);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr doc, int pageIndex);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmapWithMatrix(IntPtr bitmap, IntPtr page, ref FS_MATRIX matrix, ref FS_RECTF clipping);
}
