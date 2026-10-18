using System.Text;
using System.Text.RegularExpressions;

namespace ComicVerse.Core.Novel;

public static class TxtParser
{
    // 标题分隔符：标题文字与「：、.·」等分隔符之间常见于真实 TXT（第一章：开始 / 第一章、开始 / 第一章.开始）
    private const string TitleSep = @"[\s:：、·.．,，\-—－]";

    private static readonly Regex ChapterHeaderRegex = new(
        @"^\s*(?:第\s*[0-9零一二三四五六七八九十百千两〇]+\s*[章节回话話卷部篇集幕](?:" + TitleSep + @".*)?|(?:Chapter|Part)\s+\d+(?:" + TitleSep + @".*)?|序章|序言|楔子|番外|后记|尾声|间章)(?:" + TitleSep + @".*)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static NovelBook Parse(string text, string title)
    {
        var book = new NovelBook { Title = title };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var paragraphs = new List<(bool IsHeader, string Content)>();
        var current = new StringBuilder();
        var currentIsHeader = false;

        void Flush()
        {
            if (current.Length == 0) return;
            string content = current.ToString().Trim();
            if (content.Length > 0)
                paragraphs.Add((currentIsHeader, content));
            current.Clear();
            currentIsHeader = false;
        }

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (ChapterHeaderRegex.IsMatch(line))
            {
                Flush();
                paragraphs.Add((true, line));
                continue;
            }
            if (current.Length > 0) current.Append('\n');
            current.Append(raw.Trim());
        }
        Flush();

        // 文首目录块：连续出现、彼此之间没有任何正文的标题（如书籍开头列出的「第一章…第二章…第三章…」）。
        // 只要后面还有同名标题重复出现，就把前面这一串视为目录，跳过，避免生成一串只有标题的空章。
        int skipUntil = 0;
        if (paragraphs.Count > 0 && paragraphs[0].IsHeader)
        {
            // 文首连续的标题（中间没有正文）先整串取出
            int runEnd = 0;
            while (runEnd + 1 < paragraphs.Count && paragraphs[runEnd + 1].IsHeader) runEnd++;

            if (runEnd > 0)
            {
                // 目录块的特征：这串标题之后，还能在正文里再见到其中某个标题。
                // 注意：目录串末尾常紧跟正文首章标题（同名、中间无空行），所以本串最后一个标题
                // 不参与“目录标题”判断，realStart 从它开始算，才不会被当成目录一起丢掉。
                var titles = new HashSet<string>(StringComparer.Ordinal);
                for (int k = 0; k < runEnd; k++) titles.Add(NormalizeTitle(paragraphs[k].Content));

                int realStart = -1;
                for (int k = runEnd; k < paragraphs.Count; k++)
                {
                    if (paragraphs[k].IsHeader && titles.Contains(NormalizeTitle(paragraphs[k].Content)))
                    {
                        realStart = k;
                        break;
                    }
                }

                // 只有目录串里确有 >=2 个标题（P0..P{runEnd-1}）且正文里确实重复出现，才认定是目录
                if (realStart >= 0)
                    skipUntil = realStart;
            }
        }

        var preface = new StringBuilder();
        var chapters = new List<NovelChapter>();
        NovelChapter? chapter = null;

        for (int i = skipUntil; i < paragraphs.Count; i++)
        {
            var (isHeader, content) = paragraphs[i];
            if (isHeader)
            {
                // 每遇一个标题就新开一章；空章（只有标题、没有正文）在收尾时统一丢弃
                chapter = new NovelChapter { Title = content };
                chapters.Add(chapter);
                chapter.Blocks.Add(new NovelBlock { Text = content, IsHeading = true });
            }
            else if (chapter is null)
            {
                // 首个标题之前的正文是前言/书名作者信息，不能当成第 0 章，也不该覆盖书名
                if (preface.Length > 0) preface.Append('\n');
                preface.Append(content);
            }
            else
            {
                chapter.Blocks.Add(new NovelBlock { Text = content });
            }
        }

        // 丢弃只有标题、没有正文的空章（连续两个标题、或目录残留导致）
        var kept = new List<NovelChapter>(chapters.Count);
        foreach (var c in chapters)
        {
            if (c.Blocks.Any(b => !b.IsHeading)) kept.Add(c);
        }

        if (kept.Count == 0)
        {
            // 没有任何带正文的章节：整本作为单章，前言并入正文
            var single = new NovelChapter { Title = title };
            if (preface.Length > 0)
                single.Blocks.Add(new NovelBlock { Text = preface.ToString() });
            foreach (var c in chapters)
                single.Blocks.AddRange(c.Blocks);
            book.Chapters.Add(single);
            return book;
        }

        if (preface.Length > 0)
        {
            // 前言并入第一章正文前部，保持可读且不产生额外目录项
            kept[0].Blocks.Insert(0, new NovelBlock { Text = preface.ToString() });
        }

        if (kept.Count == 1 && !kept[0].Blocks.Any(b => b.IsHeading))
            kept[0].Title = title;

        book.Chapters.AddRange(kept);
        return book;
    }

    private static string NormalizeTitle(string s) => Regex.Replace(s, @"\s+", "").Trim();
}
