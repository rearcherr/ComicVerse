using System.IO;
using Android.Graphics;
using Path = System.IO.Path;

namespace ComicVerse.Core;

/// <summary>安卓端图片工具：与桌面端同名 API，内部改用 BitmapFactory 与 Canvas。</summary>
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

    private static Stream Seekable(Stream stream)
    {
        if (stream is MemoryStream ms && ms.CanSeek)
        {
            ms.Position = 0;
            return ms;
        }
        if (stream.CanSeek)
        {
            stream.Position = 0;
            return stream;
        }
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    /// <summary>解码为位图（对应桌面端的 DecodeFrozen）。</summary>
    public static Bitmap? Decode(Stream stream)
    {
        try
        {
            using var seekable = Seekable(stream);
            var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
            return BitmapFactory.DecodeStream(seekable, null, options);
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
            using var seekable = Seekable(stream);
            var options = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeStream(seekable, null, options);
            if (options.OutWidth <= 0 || options.OutHeight <= 0) return null;
            return (options.OutWidth, options.OutHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取图片头部若干字节推断尺寸，避免为了取尺寸解压/读取整页数据。</summary>
    public static (int Width, int Height)? GetDimensionsFromPrefix(Stream stream, out int bytesRead, int maxBytes = 384 * 1024)
    {
        bytesRead = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            var prefix = new MemoryStream();
            try
            {
                while (bytesRead < maxBytes)
                {
                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - bytesRead));
                    if (read <= 0) break;
                    prefix.Write(buffer, 0, read);
                    bytesRead += read;
                }
                if (bytesRead == 0) return null;
                prefix.Position = 0;
                return GetDimensions(prefix);
            }
            finally
            {
                prefix.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap ScaleToWidth(Bitmap source, int targetWidth)
    {
        if (source.Width <= targetWidth) return source;
        int height = Math.Max(1, (int)Math.Round(source.Height * ((double)targetWidth / source.Width)));
        var scaled = Bitmap.CreateScaledBitmap(source, targetWidth, height, true);
        return scaled ?? source;
    }

    public static byte[] EncodePng(Bitmap source)
    {
        using var ms = new MemoryStream();
        source.Compress(Bitmap.CompressFormat.Png!, 100, ms);
        return ms.ToArray();
    }
}
