using System.Text;
using System.Text.RegularExpressions;

namespace ComicVerse.Core.Novel;

public static class TxtParser
{
    private static readonly Regex ChapterHeaderRegex = new(
        @"^\s*(?:第\s*[0-9零一二三四五六七八九十百千两〇]+\s*[章节回话卷部篇集幕](?:\s+.*)?|(?:Chapter|CHAPTER|Part|PART)\s+\d+(?:\s+.*)?|序章|序言|楔子|番外|后记|尾声|间章)\s*$",
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

        var chapter = new NovelChapter { Title = title };
        bool headerSeen = false;
        foreach (var (isHeader, content) in paragraphs)
        {
            if (isHeader)
            {
                headerSeen = true;
                if (chapter.Blocks.Count > 0)
                {
                    book.Chapters.Add(chapter);
                    chapter = new NovelChapter { Title = content };
                }
                else
                {
                    chapter.Title = content;
                }
                chapter.Blocks.Add(new NovelBlock { Text = content, IsHeading = true });
            }
            else
            {
                chapter.Blocks.Add(new NovelBlock { Text = content });
            }
        }
        if (chapter.Blocks.Count > 0)
            book.Chapters.Add(chapter);

        if (book.Chapters.Count == 0)
            book.Chapters.Add(new NovelChapter { Title = title });

        if (!headerSeen && book.Chapters.Count > 1)
        {
            // 没有章节标题时合并为单章，避免无意义分章
            var merged = new NovelChapter { Title = title };
            foreach (var c in book.Chapters)
                merged.Blocks.AddRange(c.Blocks);
            book.Chapters.Clear();
            book.Chapters.Add(merged);
        }

        return book;
    }
}
