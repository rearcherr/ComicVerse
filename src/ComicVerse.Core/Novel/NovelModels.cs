namespace ComicVerse.Core.Novel;

public sealed class NovelBlock
{
    public string? Text { get; set; }
    public string? ImageEntry { get; set; }
    public bool IsHeading { get; set; }
    public bool IsImage => ImageEntry is not null;
}

public sealed class NovelChapter
{
    public string Title { get; set; } = "";
    public string? Href { get; set; }
    public List<NovelBlock> Blocks { get; set; } = new();
}

public sealed class NovelBook
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Language { get; set; } = "";
    public List<NovelChapter> Chapters { get; } = new();
}
