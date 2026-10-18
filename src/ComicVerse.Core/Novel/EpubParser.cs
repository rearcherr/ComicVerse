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
    // 必须一直匹配到 '>'（含自闭合的 "/>"），否则标签尾部会残留在正文里
    private static readonly Regex ImgRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""'](?<src>[^""']+)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HeadRegex = new(
        @"<head\b[^>]*>.*?</head>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex XmlDeclRegex = new(
        @"<\?.*?\?>", RegexOptions.Compiled | RegexOptions.Singleline);

    public NovelBook Parse(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return ParseArchive(zip, Path.GetFileNameWithoutExtension(path));
    }

    public NovelBook ParseArchive(ZipArchive zip, string fallbackTitle)
    {
        string? opfPath = FindOpfPath(zip);
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
            manifest[id] = Resolve(href, baseDir);
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

            var entry = FindEntry(zip, href);
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
            if (zip.GetEntry(entry) is null && FindEntry(zip, entry) is null) return "";
            string token = "\u0002IMG" + imagePlaceholders.Count + "\u0003";
            imagePlaceholders.Add((token, entry));
            return token;
        });

        // 块级结束标签 -> 段落分隔
        html = Regex.Replace(html, @"</(p|div|h[1-6]|li|tr|section|blockquote|article|header|footer|figure|figcaption)>",
            "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|hr)\b[^>]*/?>", "\n", RegexOptions.IgnoreCase);

        // 移除 <head>…</head> 与 XML 声明，避免 <title> 等元数据混入正文
        html = HeadRegex.Replace(html, "");
        html = XmlDeclRegex.Replace(html, "");

        // 剥离所有剩余的开始标签（此前完全缺失这一步，导致 <p>/<html> 等原样显示在正文里）
        html = TagRegex.Replace(html, "");

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
        // href 是 URL 编码的（%20 / %E4%B8%AD …），必须先解码再去查 ZipArchive 条目
        try { clean = Uri.UnescapeDataString(clean); } catch { /* 非法转义保持原样 */ }
        clean = clean.Replace('\\', '/');
        // 不能在这里直接返回 TrimStart('/')：'/' 开头的 href 是相对 *包根* 的，
        // 而 OCF 的“根”就是压缩包根，因此它仍要拼上 OPF 所在目录；
        // 否则 "/ch0.xhtml" 会解析成 "ch0.xhtml"（包根），找不到 "OEBPS/ch0.xhtml"。
        clean = clean.TrimStart('/');
        string combined = dir.Length > 0 ? dir + "/" + clean : clean;
        return NormalizeZipPath(combined);
    }

    /// <summary>把 "a/b/../c"、"a/./b" 归一化成 "a/c"、"a/b"；ZipArchive 条目名始终用 '/' 分隔。</summary>
    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (string seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>精确查找条目；找不到时回退为大小写不敏感匹配（macOS 打包的 EPUB 常见）。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is not null) return entry;
        foreach (var e in zip.Entries)
        {
            if (string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }
}
