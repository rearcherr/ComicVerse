using System.Globalization;

namespace ComicVerse.Core.Models;

public enum BookType
{
    Comic,
    Novel
}

public enum BookFormat
{
    Unknown,
    Cbz,
    Cbr,
    Cbt,
    SevenZip,
    Pdf,
    Folder,
    Txt,
    Epub
}

public sealed class Book
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public BookType Type { get; set; }
    public BookFormat Format { get; set; }
    public long FileSize { get; set; }
    public string Fingerprint { get; set; } = "";
    public int PageCount { get; set; }
    public string? CoverPath { get; set; }
    public double Progress { get; set; }
    public DateTime LastReadTime { get; set; }
    public DateTime AddedTime { get; set; }
    public int ChapterCount { get; set; }
    public string? Error { get; set; }

    public bool IsError => !string.IsNullOrWhiteSpace(Error);

    public string ProgressText => (Progress * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public string FormatText => Format switch
    {
        BookFormat.Cbz => "CBZ",
        BookFormat.Cbr => "CBR",
        BookFormat.Cbt => "CBT",
        BookFormat.SevenZip => "7Z",
        BookFormat.Pdf => "PDF",
        BookFormat.Folder => "文件夹",
        BookFormat.Txt => "TXT",
        BookFormat.Epub => "EPUB",
        _ => "未知"
    };
}

public sealed class ReadingProgress
{
    public long BookId { get; set; }
    public int PageIndex { get; set; }
    public double ScrollOffset { get; set; }
    public int ChapterIndex { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Bookmark
{
    public long Id { get; set; }
    public long BookId { get; set; }
    public int PageIndex { get; set; }
    public int ChapterIndex { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>阅读排版偏好（小说）。</summary>
public sealed class NovelViewSettings
{
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 18;
    public double LineSpacing { get; set; } = 1.7;
    public double ParagraphSpacing { get; set; } = 8;
    public string TextColor { get; set; } = "#E8E8F4";
    public string Background { get; set; } = "#1A1A2E";
    public double PageMargin { get; set; } = 56;

    public NovelViewSettings Clone() => (NovelViewSettings)MemberwiseClone();
}
