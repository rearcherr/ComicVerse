using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Core.Services;

/// <summary>封面缩略图生成：漫画取首页缩略图；小说生成渐变文字封面。</summary>
public static class CoverGenerator
{
    public const int CoverWidth = 320;
    public const int CoverHeight = 440;

    private static readonly LinearGradientBrush AccentGradient = CreateGradient();

    public static string? GenerateFromImage(Stream imageStream, string coverDir, string key)
    {
        try
        {
            var src = ImageHelper.DecodeFrozen(imageStream);
            if (src is null) return null;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(AccentGradient, null, new Rect(0, 0, CoverWidth, CoverHeight));
                double scale = Math.Min((double)(CoverWidth - 24) / src.PixelWidth, (double)(CoverHeight - 24) / src.PixelHeight);
                int w = Math.Max(1, (int)(src.PixelWidth * scale));
                int h = Math.Max(1, (int)(src.PixelHeight * scale));
                dc.DrawImage(src, new Rect((CoverWidth - w) / 2.0, (CoverHeight - h) / 2.0, w, h));
            }
            return Save(visual, coverDir, key);
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
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(AccentGradient, null, new Rect(0, 0, CoverWidth, CoverHeight));

                // 装饰圆与星芒
                var circleBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
                circleBrush.Freeze();
                dc.DrawEllipse(circleBrush, null, new Point(270, 70), 90, 90);
                dc.DrawEllipse(circleBrush, null, new Point(40, 380), 110, 110);
                DrawSparkles(dc);

                var textBrush = new SolidColorBrush(Colors.White);
                textBrush.Freeze();
                string font = "Microsoft YaHei UI";
                double size = Math.Clamp(64.0 - title.Length * 1.4, 20, 42);
                var ft = new FormattedText(
                    title,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size, textBrush, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
                ft.MaxTextWidth = CoverWidth - 48;
                ft.MaxLineCount = 4;
                dc.DrawText(ft, new Point(24, 180));

                if (!string.IsNullOrEmpty(subtitle))
                {
                    var subBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
                    subBrush.Freeze();
                    var sub = new FormattedText(
                        subtitle,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                        14, subBrush, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
                    dc.DrawText(sub, new Point(24, CoverHeight - 64));
                }
            }
            return Save(visual, coverDir, key);
        }
        catch (Exception ex)
        {
            Log.Error("文字封面生成失败", ex);
            return null;
        }
    }

    private static void DrawSparkles(DrawingContext dc)
    {
        var star = new StreamGeometry();
        var r = 7.0;
        var center = new Point(60, 60);
        var points = new[]
        {
            new Point(center.X, center.Y - r), new Point(center.X + r * 0.35, center.Y - r * 0.35),
            new Point(center.X + r, center.Y), new Point(center.X + r * 0.35, center.Y + r * 0.35),
            new Point(center.X, center.Y + r), new Point(center.X - r * 0.35, center.Y + r * 0.35),
            new Point(center.X - r, center.Y), new Point(center.X - r * 0.35, center.Y - r * 0.35)
        };
        using (var ctx = star.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points, true, true);
        }
        star.Freeze();
        var brush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        brush.Freeze();
        dc.DrawGeometry(brush, null, star);
    }

    private static string Save(DrawingVisual visual, string coverDir, string key)
    {
        Directory.CreateDirectory(coverDir);
        var rtb = new RenderTargetBitmap(CoverWidth, CoverHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        string path = Path.Combine(coverDir, key + ".png");
        File.WriteAllBytes(path, ImageHelper.EncodePng(rtb));
        return path;
    }

    private static LinearGradientBrush CreateGradient()
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString("#FF6B9D"),
            (Color)ColorConverter.ConvertFromString("#C44CEC"),
            45);
        brush.Freeze();
        return brush;
    }
}
