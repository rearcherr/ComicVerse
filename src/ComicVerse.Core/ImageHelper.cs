using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Core;

public static class ImageHelper
{
    public static readonly string[] ImageExtensions =
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".bmp", ".gif",
        ".tif", ".tiff", ".webp", ".avif", ".heic"
    };

    public static bool IsImageFile(string name)
    {
        try
        {
            return ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把任意可读流解码为冻结的 BitmapSource（可跨线程安全使用）。</summary>
    public static BitmapSource? DecodeFrozen(Stream stream)
    {
        try
        {
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>只读取图片尺寸（不完整解码，用于布局）。</summary>
    public static (int Width, int Height)? GetDimensions(Stream stream)
    {
        try
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource ScaleToWidth(BitmapSource source, int targetWidth)
    {
        if (source.PixelWidth <= targetWidth) return source;
        double scale = (double)targetWidth / source.PixelWidth;
        int w = targetWidth;
        int h = Math.Max(1, (int)(source.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, w, h));
        }
        var drawn = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        drawn.Render(visual);
        drawn.Freeze();
        return drawn;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
