using System.IO;
using Android.Graphics;
using Path = System.IO.Path;

namespace ComicVerse.Core.Services;

/// <summary>封面缩略图生成（安卓）：漫画取首页缩略图；小说生成渐变文字封面。</summary>
public static class CoverGenerator
{
    public const int CoverWidth = 320;
    public const int CoverHeight = 440;

    public static string? GenerateFromImage(Stream imageStream, string coverDir, string key)
    {
        try
        {
            using var src = ImageHelper.Decode(imageStream);
            if (src is null) return null;

            using var bmp = Bitmap.CreateBitmap(CoverWidth, CoverHeight, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(bmp);
            using var paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
            DrawGradient(canvas, paint);

            double scale = Math.Min((double)(CoverWidth - 24) / src.Width, (double)(CoverHeight - 24) / src.Height);
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));
            float left = (CoverWidth - w) / 2f;
            float top = (CoverHeight - h) / 2f;
            var dst = new RectF(left, top, left + w, top + h);
            canvas.DrawBitmap(src, null, dst, paint);
            return Save(bmp, coverDir, key);
        }
        catch (Exception ex)
        {
            Log.Error("漫画封面生成失败", ex);
            return null;
        }
    }

    public static string? GenerateTextCover(string title, string subtitle, string coverDir, string key)
    {
        try
        {
            using var bmp = Bitmap.CreateBitmap(CoverWidth, CoverHeight, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(bmp);
            using var paint = new Paint(PaintFlags.AntiAlias);
            DrawGradient(canvas, paint);

            // 装饰圆与星芒
            paint.SetShader(null);
            paint.Color = Color.Argb(36, 255, 255, 255);
            canvas.DrawCircle(270, 70, 90, paint);
            canvas.DrawCircle(40, 380, 110, paint);
            DrawSparkle(canvas, paint, 60, 60, 7);

            float size = (float)Math.Clamp(64.0 - title.Length * 1.4, 20, 42);
            using var textPaint = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { Color = Color.White };
            textPaint.SetTypeface(Typeface.Create("sans-serif", TypefaceStyle.Bold));
            textPaint.TextSize = size;
            float lineHeight = size * 1.28f;
            float y = 180;
            foreach (string line in WrapText(title, textPaint, CoverWidth - 48, 4))
            {
                canvas.DrawText(line, 24, y + textPaint.TextSize, textPaint);
                y += lineHeight;
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                using var subPaint = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText)
                {
                    Color = Color.Argb(220, 255, 255, 255)
                };
                subPaint.SetTypeface(Typeface.Create("sans-serif", TypefaceStyle.Normal));
                subPaint.TextSize = 15;
                canvas.DrawText(subtitle, 24, CoverHeight - 32, subPaint);
            }
            return Save(bmp, coverDir, key);
        }
        catch (Exception ex)
        {
            Log.Error("文字封面生成失败", ex);
            return null;
        }
    }

    private static void DrawGradient(Canvas canvas, Paint paint)
    {
        using var shader = new LinearGradient(
            0, 0, CoverWidth, CoverHeight,
            new[] { Color.ParseColor("#FF6B9D").ToArgb(), Color.ParseColor("#C44CEC").ToArgb() },
            null, Shader.TileMode.Clamp);
        paint.SetShader(shader);
        canvas.DrawRect(0, 0, CoverWidth, CoverHeight, paint);
        paint.SetShader(null);
    }

    private static void DrawSparkle(Canvas canvas, Paint paint, float cx, float cy, float r)
    {
        using var path = new Android.Graphics.Path();
        path.MoveTo(cx, cy - r);
        path.LineTo(cx + r * 0.35f, cy - r * 0.35f);
        path.LineTo(cx + r, cy);
        path.LineTo(cx + r * 0.35f, cy + r * 0.35f);
        path.LineTo(cx, cy + r);
        path.LineTo(cx - r * 0.35f, cy + r * 0.35f);
        path.LineTo(cx - r, cy);
        path.LineTo(cx - r * 0.35f, cy - r * 0.35f);
        path.Close();
        paint.Color = Color.Argb(150, 255, 255, 255);
        canvas.DrawPath(path, paint);
    }

    private static List<string> WrapText(string text, Paint paint, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            builder.Append(c);
            if (paint.MeasureText(builder.ToString()) <= maxWidth) continue;
            if (builder.Length <= 1)
            {
                lines.Add(builder.ToString());
                builder.Clear();
                continue;
            }
            builder.Length -= 1;
            lines.Add(builder.ToString());
            builder.Clear();
            builder.Append(c);
            if (lines.Count >= maxLines)
            {
                lines[^1] = lines[^1] + "…";
                return lines;
            }
        }
        if (builder.Length > 0 && lines.Count < maxLines)
            lines.Add(builder.ToString().Trim());
        return lines;
    }

    private static string Save(Bitmap bitmap, string coverDir, string key)
    {
        Directory.CreateDirectory(coverDir);
        string path = Path.Combine(coverDir, key + ".png");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, fs);
        }
        return path;
    }
}
