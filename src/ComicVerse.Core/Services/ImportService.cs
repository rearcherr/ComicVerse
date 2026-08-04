using System.IO;
using System.Text;
using ComicVerse.Core.Comics;
using ComicVerse.Core.Models;
using ComicVerse.Core.Novel;

namespace ComicVerse.Core.Services;

public sealed class ImportProgress
{
    public int Total { get; set; }
    public int Done { get; set; }
    public string Current { get; set; } = "";
}

public sealed class ImportResult
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public List<string> Failed { get; } = new();
}

/// <summary>
/// 导入服务：单文件/文件夹（含混合格式自动识别），按指纹去重，
/// 漫画生成封面缩略图，小说解析章节，失败记录不崩溃（US-23/US-24）。
/// </summary>
public sealed class ImportService
{
    private readonly LibraryService _library;

    public ImportService(LibraryService library)
    {
        _library = library;
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> paths, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var work = new List<string>();
        foreach (string path in list)
        {
            if (Directory.Exists(path))
            {
                work.AddRange(ExpandFolder(path));
            }
            else if (File.Exists(path))
            {
                work.Add(path);
            }
        }

        var reporter = new ImportProgress { Total = work.Count };
        progress?.Report(reporter);
        foreach (string item in work)
        {
            ct.ThrowIfCancellationRequested();
            reporter.Current = item;
            reporter.Done++;
            progress?.Report(reporter);
            try
            {
                bool updated = await Task.Run(() => ImportOne(item, result), ct).ConfigureAwait(true);
                if (updated) result.Updated++;
                else result.Imported++;
            }
            catch (Exception ex)
            {
                Log.Error("导入失败: " + item, ex);
                result.Failed.Add(item + " — " + (ex is ComicSourceException or InvalidDataException ? ex.Message : "解析失败，请检查文件是否损坏"));
            }
        }
        return result;
    }

    private List<string> ExpandFolder(string folder)
    {
        var files = new List<string>();
        try
        {
            var all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && !f.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var images = all.Where(ImageHelper.IsImageFile).ToList();
            var novels = all.Where(f => IsNovelFile(f)).ToList();

            if (images.Count > 0)
            {
                // 图片在根目录 → 整个文件夹作为一本漫画；嵌套子目录的图片也算入
                files.Add(folder);
            }
            else if (novels.Count == 0)
            {
                // 没有任何可识别文件，仍尝试作为漫画导入以给出友好错误
                files.Add(folder);
            }

            // 文件夹里的 txt/epub 单独作为小说导入（混合格式自动识别）
            foreach (var n in novels)
                files.Add(n);
        }
        catch (Exception ex)
        {
            Log.Error("扫描文件夹失败: " + folder, ex);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool ImportOne(string path, ImportResult result)
    {
        if (Directory.Exists(path))
        {
            return ImportComicFolder(path, result);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (IsNovelFile(path))
        {
            return ImportNovel(path);
        }
        if (ImageHelper.IsImageFile(path) || IsComicFile(ext))
        {
            return ImportComic(path);
        }
        throw new ComicSourceException("不支持的文件格式: " + ext);
    }

    private bool ImportComicFolder(string folder, ImportResult result)
    {
        var images = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(ImageHelper.IsImageFile)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .ToList();
        if (images.Count == 0)
            throw new ComicSourceException("文件夹内没有支持的图片文件");

        using var source = new FolderComicSource(folder);
        return SaveComicBook(new Book
        {
            Title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
            FilePath = folder,
            Type = BookType.Comic,
            Format = BookFormat.Folder,
            PageCount = source.PageCount
        }, source, folder);
    }

    private bool ImportComic(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        using var source = ComicSourceFactory.Create(file);
        var format = ext switch
        {
            ".cbz" or ".zip" => BookFormat.Cbz,
            ".cbr" or ".rar" => BookFormat.Cbr,
            ".cbt" or ".tar" => BookFormat.Cbt,
            ".cb7" or ".7z" => BookFormat.SevenZip,
            ".pdf" => BookFormat.Pdf,
            _ => BookFormat.Folder
        };
        return SaveComicBook(new Book
        {
            Title = Path.GetFileNameWithoutExtension(file),
            FilePath = file,
            Type = BookType.Comic,
            Format = format,
            PageCount = source.PageCount
        }, source, file);
    }

    private bool SaveComicBook(Book book, IComicSource source, string path)
    {
        var fi = new FileInfo(path);
        book.FileSize = Directory.Exists(path) ? 0 : fi.Length;
        book.Fingerprint = Directory.Exists(path) ? "dir:" + path.ToLowerInvariant() : Fingerprint.Compute(path);

        var existing = _library.GetBookByFingerprint(book.Fingerprint);
        if (existing is not null)
        {
            existing.FilePath = path;
            existing.Title = book.Title;
            existing.FileSize = book.FileSize;
            existing.PageCount = book.PageCount;
            existing.Error = null;
            _library.UpsertBook(existing);
            return true; // updated
        }

        string coverKey = book.Fingerprint[..Math.Min(16, book.Fingerprint.Length)];
        try
        {
            using var first = source.GetPageStream(0);
            book.CoverPath = CoverGenerator.GenerateFromImage(first, _library.CoverDir, coverKey);
        }
        catch (Exception ex)
        {
            Log.Error("首图缩略图失败", ex);
        }

        book.Id = _library.UpsertBook(book);
        return false;
    }

    private bool ImportNovel(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        string title = Path.GetFileNameWithoutExtension(file);
        NovelBook novel;

        if (ext == ".epub")
        {
            novel = new EpubParser().Parse(file);
        }
        else
        {
            byte[] bytes = File.ReadAllBytes(file);
            var enc = EncodingDetector.Detect(bytes);
            string text = enc.GetString(bytes);
            novel = TxtParser.Parse(text, title);
        }

        var book = new Book
        {
            Title = novel.Title.Length > 0 ? novel.Title : title,
            FilePath = file,
            Type = BookType.Novel,
            Format = ext == ".epub" ? BookFormat.Epub : BookFormat.Txt,
            FileSize = new FileInfo(file).Length,
            PageCount = novel.Chapters.Count,
            ChapterCount = novel.Chapters.Count
        };
        book.Fingerprint = Fingerprint.Compute(file);

        var existing = _library.GetBookByFingerprint(book.Fingerprint);
        if (existing is not null)
        {
            existing.FilePath = file;
            existing.Title = book.Title;
            existing.FileSize = book.FileSize;
            existing.PageCount = book.PageCount;
            existing.ChapterCount = book.ChapterCount;
            existing.Error = null;
            _library.UpsertBook(existing);
            return true;
        }

        string coverKey = book.Fingerprint[..Math.Min(16, book.Fingerprint.Length)];
        book.CoverPath = CoverGenerator.GenerateTextCover(book.Title, ext.ToUpperInvariant(), _library.CoverDir, coverKey);
        book.Id = _library.UpsertBook(book);
        return false;
    }

    private static bool IsNovelFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".txt" or ".epub";
    }

    private static bool IsComicFile(string ext) =>
        ext is ".cbz" or ".zip" or ".cbr" or ".rar" or ".cbt" or ".tar" or ".cb7" or ".7z" or ".pdf";
}
