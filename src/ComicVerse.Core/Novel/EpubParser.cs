using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ComicVerse.Core.Novel;

/// <summary>
/// 轻量 EPUB 解析：读取 OPF/spine 章节顺序，将 XHTML 转成段落文本与内嵌图片块。
/// </summary>
public sealed class EpubParser
{
    private static readonly Regex TagRegex = new(@"<[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ImgRegex = new(
        @"<img\b[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public NovelBook Parse(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return ParseArchive(zip, Path.GetFileNameWithoutExtension(path));
    }

    public NovelBook ParseArchive(ZipArchive zip, string fallbackTitle)
    {
        string opfPath = FindOpfPath(zip);
        if (opfPath is null)
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml 或 OPF 文件");

        using var opfStream = zip.GetEntry(opfPath)!.Open();
        var opf = XDocument.Load(opfStream);
        XNamespace ns = opf.Root!.Name.Namespace;

        string baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
        var manifest = new Dictionary<string, string>(); // id -> href
        var mediaTypes = new Dictionary<string, string>();
        foreach (var item in opf.Descendants(ns + "item"))
        {
            string id = (string?)item.Attribute("id") ?? "";
            string href = (string?)item.Attribute("href") ?? "";
            manifest[id] = baseDir.Length > 0 ? baseDir + "/" + href : href;
            mediaTypes[id] = (string?)item.Attribute("media-type") ?? "";
        }

        string? title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value?.Trim();
        string? author = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value?.Trim();
        string? language = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "language")?.Value?.Trim();

        var book = new NovelBook
        {
            Title = string.IsNullOrWhiteSpace(title) ? fallbackTitle : WebUtility.HtmlDecode(title),
            Author = author is null ? "" : WebUtility.HtmlDecode(author),
            Language = language ?? ""
        };

        var spineIds = opf.Descendants(ns + "itemref")
            .Select(e => (string?)e.Attribute("idref") ?? "")
            .Where(id => manifest.ContainsKey(id))
            .ToList();
        if (spineIds.Count == 0)
            spineIds = manifest.Keys.ToList();

        foreach (string id in spineIds)
        {
            string href = manifest[id];
            string mediaType = mediaTypes[id];
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("xml"))
                continue;

            var entry = zip.GetEntry(href);
            if (entry is null) continue;

            string html;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                html = reader.ReadToEnd();

            string chapterTitle = TryGetFirstHeading(html) ?? Path.GetFileNameWithoutExtension(entry.Name);
            var blocks = ParseHtml(html, href, zip);
            if (blocks.Count == 0) continue;

            book.Chapters.Add(new NovelChapter { Title = chapterTitle, Href = href, Blocks = blocks });
        }

        if (book.Chapters.Count == 0)
            throw new InvalidDataException("EPUB 中没有可解析的章节");
        return book;
    }

    private static string? FindOpfPath(ZipArchive zip)
    {
        var container = zip.GetEntry("META-INF/container.xml");
        if (container is null) return null;
        using var s = container.Open();
        var doc = XDocument.Load(s);
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
    }

    private static string? TryGetFirstHeading(string html)
    {
        var m = Regex.Match(html, @"<h[1-6][^>]*>\s*(.*?)\s*</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        string text = Regex.Replace(m.Groups[1].Value, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Trim();
        return text.Length > 0 && text.Length < 80 ? text : null;
    }

    private static List<NovelBlock> ParseHtml(string html, string href, ZipArchive zip)
    {
        string dir = (Path.GetDirectoryName(href) ?? "").Replace('\\', '/');

        // 移除 script/style
        html = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);

        // 图片标签替换为占位标记
        var imagePlaceholders = new List<(string Token, string Entry)>();
        html = ImgRegex.Replace(html, m =>
        {
            string src = m.Groups["src"].Value.Trim();
            string entry = Resolve(src, dir);
            if (zip.GetEntry(entry) is null) return "";
            string token = "\u0002IMG" + imagePlaceholders.Count + "\u0003";
            imagePlaceholders.Add((token, entry));
            return token;
        });

        // 块级结束标签 -> 段落分隔
        html = Regex.Replace(html, @"</(p|div|h[1-6]|li|tr|section|blockquote|article|header|footer|figure|figcaption)>",
            "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|hr)\b[^>]*/?>", "\n", RegexOptions.IgnoreCase);

        string text = WebUtility.HtmlDecode(html);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var blocks = new List<NovelBlock>();
        var para = new StringBuilder();

        void Flush()
        {
            string content = para.ToString().Trim();
            if (content.Length == 0) return;
            foreach (var (token, entry) in imagePlaceholders)
            {
                int idx;
                while ((idx = content.IndexOf(token, StringComparison.Ordinal)) >= 0)
                {
                    string before = content[..idx].Trim();
                    if (before.Length > 0)
                        blocks.Add(new NovelBlock { Text = before });
                    blocks.Add(new NovelBlock { ImageEntry = entry });
                    content = content[(idx + token.Length)..].Trim();
                }
            }
            if (content.Length > 0)
                blocks.Add(new NovelBlock { Text = content });
            para.Clear();
        }

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(trimmed);
        }
        Flush();
        return blocks;
    }

    private static string Resolve(string src, string dir)
    {
        string clean = src.Split('?')[0].Split('#')[0].Trim();
        if (clean.StartsWith('/'))
            return clean.TrimStart('/');
        if (dir.Length > 0)
            return dir + "/" + clean;
        return clean;
    }
}
