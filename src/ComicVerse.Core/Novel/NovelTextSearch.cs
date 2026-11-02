namespace ComicVerse.Core.Novel;

/// <summary>位置使用原始文本块中的 UTF-16 字符索引，可直接用于 WPF Run/TextPointer 定位。</summary>
public sealed record NovelSearchMatch(
    int ChapterIndex, int BlockIndex, int Start, int Length, string ChapterTitle, string Preview);

public sealed record NovelSearchResult(IReadOnlyList<NovelSearchMatch> Matches, bool IsTruncated);

/// <summary>对已解析的小说文本块做字面搜索，不解释用户输入为正则表达式。</summary>
public static class NovelTextSearch
{
    public const int MaximumResults = 500;
    private const int ScanChunkSize = 32 * 1024;

    /// <summary>后台搜索；搜索期间调用方不应修改书的章节或正文块集合。</summary>
    public static Task<NovelSearchResult> SearchAsync(NovelBook book, string? query,
        int maxResults = MaximumResults, CancellationToken ct = default)
        => Task.Run(() => Search(book, query, maxResults, ct), ct);

    /// <summary>
    /// 按章节、文本块、字符位置顺序返回不重叠匹配，忽略大小写，跳过图片块。
    /// 仅在还有更多命中时标记截断；空白查询返回空结果。
    /// </summary>
    public static NovelSearchResult Search(NovelBook book, string? query,
        int maxResults = MaximumResults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (maxResults is < 1 or > MaximumResults)
            throw new ArgumentOutOfRangeException(nameof(maxResults), $"结果上限必须为 1–{MaximumResults}");
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return new NovelSearchResult(Array.Empty<NovelSearchMatch>(), false);

        var matches = new List<NovelSearchMatch>();
        for (int chapterIndex = 0; chapterIndex < book.Chapters.Count; chapterIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = book.Chapters[chapterIndex];
            for (int blockIndex = 0; blockIndex < chapter.Blocks.Count; blockIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var block = chapter.Blocks[blockIndex];
                if (block.IsImage || string.IsNullOrEmpty(block.Text)) continue;
                string text = block.Text;
                int start = 0;
                while (start <= text.Length - query.Length)
                {
                    int found = FindNext(text, query, start, ct);
                    if (found < 0) break;
                    if (matches.Count == maxResults)
                        return new NovelSearchResult(matches.AsReadOnly(), true);
                    matches.Add(new NovelSearchMatch(chapterIndex, blockIndex, found, query.Length,
                        chapter.Title, MakePreview(text, found, query.Length)));
                    start = found + query.Length;
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return new NovelSearchResult(matches.AsReadOnly(), false);
    }

    private static int FindNext(string text, string query, int start, CancellationToken ct)
    {
        int lastStart = text.Length - query.Length;
        while (start <= lastStart)
        {
            ct.ThrowIfCancellationRequested();
            int candidates = Math.Min(ScanChunkSize, lastStart - start + 1);
            // 多读 query.Length - 1 个字符，保证跨分段边界的字面匹配不被漏掉。
            int count = query.Length - 1 + candidates;
            int found = text.IndexOf(query, start, count, StringComparison.OrdinalIgnoreCase);
            if (found >= 0) return found;
            start += candidates;
        }
        return -1;
    }

    private static string MakePreview(string text, int start, int length)
    {
        int first = Math.Max(0, start - 40);
        int available = text.Length - first;
        int count = (int)Math.Min(available, Math.Min(160L, (long)length + 80));
        int end = first + count;
        // 摘要边界不拆开代理项对；命中坐标始终保持原始文本的精确位置。
        if (first > 0 && char.IsLowSurrogate(text[first]) && char.IsHighSurrogate(text[first - 1])) first--;
        if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end++;
        string preview = text[first..end].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return (first > 0 ? "…" : "") + preview + (end < text.Length ? "…" : "");
    }
}
