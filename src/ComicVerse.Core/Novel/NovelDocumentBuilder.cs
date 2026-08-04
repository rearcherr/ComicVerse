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
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily(s.FontFamily),
            FontSize = s.FontSize,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TextColor)),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Background)),
            PagePadding = new Thickness(s.PageMargin, s.PageMargin, s.PageMargin, s.PageMargin),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
            LineHeight = s.FontSize * s.LineSpacing
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
                LineHeight = s.FontSize * s.LineSpacing
            };
            if (block.IsHeading)
            {
                p.FontSize = s.FontSize + 8;
                p.FontWeight = FontWeights.Bold;
                p.TextAlignment = TextAlignment.Center;
                p.Margin = new Thickness(0, s.ParagraphSpacing * 2, 0, s.ParagraphSpacing * 2);
            }
            else if (s.FontSize >= 14)
            {
                // 中文排版习惯：段首缩进两字符
                p.TextIndent = s.FontSize * 2;
            }
            doc.Blocks.Add(p);
            firstBlock = false;
        }
        return doc;
    }

    private ImageSource? TryLoadImage(string entry, NovelViewSettings s)
    {
        try
        {
            using var stream = _imageResolver(entry);
            if (stream.Length == 0) return null;
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }
}
