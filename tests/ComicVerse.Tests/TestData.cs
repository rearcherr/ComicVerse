using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Tests;

public static class TestData
{
    public static void MakePng(string path, int width, int height, Color color, string label)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(color);
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                Math.Max(12, height * 0.07), new SolidColorBrush(Colors.White), 1.0)
            {
                MaxTextWidth = width - 24
            };
            dc.DrawText(ft, new Point(12, height / 2.0 - ft.Height / 2));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static void MakeCbz(string path, IEnumerable<(string Name, string ImagePath)> images)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, img) in images)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var es = entry.Open();
            using var istr = File.OpenRead(img);
            istr.CopyTo(es);
        }
    }

    public static void MakeTar(string path, IEnumerable<(string Name, string ImagePath)> images)
    {
        using var fs = File.Create(path);
        using var writer = new System.Formats.Tar.TarWriter(fs, System.Formats.Tar.TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var (name, img) in images)
        {
            writer.WriteEntry(img, name);
        }
    }

    public static void MakeEpub(string path, string title, string author, List<(string ChapterTitle, string Html)> chapters)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var container = zip.CreateEntry("META-INF/container.xml");
        using (var s = container.Open())
        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
        {
            w.Write("<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        }

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (int i = 0; i < chapters.Count; i++)
        {
            manifest.Append($"<item id=\"c{i}\" href=\"c{i}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{i}\"/>");
        }

        var opf = zip.CreateEntry("OEBPS/content.opf");
        using (var s = opf.Open())
        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
        {
            w.Write($"<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title><dc:creator>{author}</dc:creator></metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>");
        }

        for (int i = 0; i < chapters.Count; i++)
        {
            var xh = zip.CreateEntry($"OEBPS/c{i}.xhtml");
            using (var s = xh.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            {
                w.Write($"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{chapters[i].ChapterTitle}</title></head><body><h1>{chapters[i].ChapterTitle}</h1>{chapters[i].Html}</body></html>");
            }
        }
    }

    public static void MakeMinimalPdf(string path, int pageWidth = 612, int pageHeight = 792)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length 47 >>\nstream\nBT /F1 24 Tf 72 720 Td (Hello ComicVerse) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        long xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), new ASCIIEncoding());
    }

    /// <summary>生成多页 PDF（每页一段文字），用于测试远距离跳页。</summary>
    public static void MakeMultiPagePdf(string path, int pageCount, int pageWidth = 612, int pageHeight = 792)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}] /Count {pageCount} >>"
        };
        int fontObj = pageCount * 2 + 3;
        for (int i = 0; i < pageCount; i++)
        {
            int contentObj = pageCount + 3 + i;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
        }
        for (int i = 0; i < pageCount; i++)
        {
            string streamText = $"BT /F1 20 Tf 72 720 Td (Page {i + 1}) Tj ET";
            byte[] bytes = Encoding.ASCII.GetBytes(streamText);
            objects.Add($"<< /Length {bytes.Length} >>\nstream\n{streamText}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        long xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), new ASCIIEncoding());
    }
}
