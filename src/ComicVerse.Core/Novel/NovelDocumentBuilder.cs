using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.Core.Models;

namespace ComicVerse.Core.Novel;

/// <summary>把章节块构建成 WPF FlowDocument（用于翻页/滚动阅读）。</summary>
public sealed class NovelDocumentBuilder
{
    private readonly Func<string, Stream> _imageResolver;

    public NovelDocumentBuilder(Func<string, Stream>? imageResolver = null)
    {
        _imageResolver = imageResolver ?? (_ => Stream.Null);
    }

    public FlowDocument Build(NovelChapter chapter, NovelViewSettings s)
    {
        // FontFamily / ColorConverter.ConvertFromString 都是静态转换器：
        // 用户手输了非法颜色（设置页允许输入十六进制色）或系统缺字体时会抛异常，
        // 结果是"排版设置坏了 → 整本书打不开"。这里全部兜底，让排版退化而不是崩。
        double fontSize = s.FontSize > 0 ? s.FontSize : 18;
        double lineHeight = fontSize * (s.LineSpacing > 0 ? s.LineSpacing : 1.7);
        var doc = new FlowDocument
        {
            FontFamily = SafeFont(s.FontFamily),
            FontSize = fontSize,
            Foreground = new SolidColorBrush(SafeColor(s.TextColor, Color.FromRgb(0xE8, 0xE8, 0xF4))),
            Background = new SolidColorBrush(SafeColor(s.Background, Color.FromRgb(0x1A, 0x1A, 0x2E))),
            PagePadding = new Thickness(s.PageMargin, s.PageMargin, s.PageMargin, s.PageMargin),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
            LineHeight = lineHeight
        };

        bool firstBlock = true;
        foreach (var block in chapter.Blocks)
        {
            if (block.IsImage)
            {
                var img = TryLoadImage(block.ImageEntry!, s);
                if (img is not null)
                {
                    var container = new BlockUIContainer(new Border
                    {
                        Child = new Image { Source = img, Stretch = Stretch.Uniform, MaxWidth = 1400 },
                        Margin = new Thickness(0, s.ParagraphSpacing, 0, s.ParagraphSpacing)
                    });
                    doc.Blocks.Add(container);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var p = new Paragraph(new Run(block.Text))
            {
                Margin = new Thickness(0, firstBlock ? 0 : s.ParagraphSpacing, 0, 0),
                LineHeight = lineHeight
            };
            if (block.IsHeading)
            {
                p.FontSize = fontSize + 8;
                p.FontWeight = FontWeights.Bold;
                p.TextAlignment = TextAlignment.Center;
                p.Margin = new Thickness(0, s.ParagraphSpacing * 2, 0, s.ParagraphSpacing * 2);
            }
            else if (fontSize >= 14)
            {
                // 中文排版习惯：段首缩进两字符
                p.TextIndent = fontSize * 2;
            }
            doc.Blocks.Add(p);
            firstBlock = false;
        }
        return doc;
    }

    /// <summary>字体名非法/系统缺失时回退到默认字体，而不是让 FlowDocument 构造抛异常。</summary>
    private static FontFamily SafeFont(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new FontFamily("Microsoft YaHei UI");
        try
        {
            return new FontFamily(name);
        }
        catch
        {
            return new FontFamily("Microsoft YaHei UI");
        }
    }

    /// <summary>颜色字符串非法时回退到默认色（否则 ConvertFromString 抛异常 → 书打不开）。</summary>
    private static Color SafeColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch
        {
            // 落到 fallback
        }
        return fallback;
    }

    private ImageSource? TryLoadImage(string entry, NovelViewSettings s)
    {
        try
        {
            using var stream = _imageResolver(entry);
            if (stream.Length == 0) return null;
            // 统一走 DecodeFrozen：它带 OnLoad + Freeze + 异常兜底，
            // 直接 BitmapFrame.Create 在某些编码（如 CMYK JPEG）上会抛异常导致整章图片丢失。
            return ImageHelper.DecodeFrozen(stream);
        }
        catch
        {
            return null;
        }
    }
}
