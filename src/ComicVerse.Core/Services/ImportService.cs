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

    /// <summary>每多少本提交一次事务（兼顾"半成品书架"与长时间持锁）。</summary>
    private const int BatchSize = 50;

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

        int total = work.Count;
        int done = 0;
        // 每次 Report 一个快照：progress 回调在 UI 线程异步执行，复用同一个实例会让状态栏读到"未来的 Done"
        Report(progress, new ImportProgress { Total = total, Done = 0, Current = "" });

        // 一批导入放在一个事务里：中途取消/崩溃时整批回滚，不会留下"导了一半"的书架。
        // 每 BatchSize 本提交一次，避免一个几千本的大文件夹长时间持有写锁。
        var batch = _library.BeginTransaction();
        int inBatch = 0;
        try
        {
            foreach (string item in work)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                Report(progress, new ImportProgress { Total = total, Done = done, Current = item });
                try
                {
                    bool updated = await Task.Run(() => ImportOne(item, result), ct).ConfigureAwait(true);
                    if (updated) result.Updated++;
                    else result.Imported++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error("导入失败: " + item, ex);
                    result.Failed.Add(item + " — " + (ex is ComicSourceException or InvalidDataException ? ex.Message : "解析失败，请检查文件是否损坏"));
                }

                if (++inBatch >= BatchSize)
                {
                    batch.Commit();
                    batch.Dispose();
                    inBatch = 0;
                    batch = _library.BeginTransaction();
                }
            }
            batch.Commit();
        }
        finally
        {
            batch.Dispose();   // 未 Commit 的话这里回滚
        }
        return result;
    }

    private static void Report(IProgress<ImportProgress>? progress, ImportProgress snapshot) => progress?.Report(snapshot);

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
            var archives = all.Where(f => IsComicFile(Path.GetExtension(f).ToLowerInvariant())).ToList();
            bool hasRootImages = images.Any(f => string.Equals(Path.GetDirectoryName(f), folder, StringComparison.OrdinalIgnoreCase));

            // 根目录就是图片 → 整个文件夹作为一本漫画；
            // 只有嵌套图片且夹内没有压缩包/PDF 时，同样按一本漫画导入（保持原有习惯）；
            // 夹内若有压缩包/PDF，则它们才是真正的书，不再把整个文件夹合并成一本
            if (hasRootImages || (archives.Count == 0 && (images.Count > 0 || novels.Count == 0)))
            {
                files.Add(folder);
            }

            // 文件夹里的漫画压缩包 / PDF 各自作为一本书导入（此前会被整体忽略）
            foreach (var a in archives)
                files.Add(a);

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

        // 1) 同一路径（大小写不敏感，和 books.path 的 NOCASE 唯一索引一致）= 同一本书：
        //    内容更新了就刷新元数据，重新导入就当作正常更新（不能因为"文件已存在"就当成副本跳过）。
        var samePath = _library.GetBookByPathNoCase(path);
        if (samePath is not null)
        {
            samePath.FilePath = path;
            samePath.Title = book.Title;
            samePath.FileSize = book.FileSize;
            samePath.PageCount = book.PageCount;
            samePath.Fingerprint = book.Fingerprint;
            samePath.Format = book.Format;
            samePath.Error = null;
            _library.UpsertBook(samePath);
            return true; // updated
        }

        // 2) 路径不同但内容相同：只有当原路径**确实已经不在了**才认定为"用户移动/改名了文件"，
        //    把那一行接回来（保留阅读进度）；原路径还在 → 这是用户的另一份副本，跳过，
        //    绝不改写原书的 FilePath（否则原路径那份在书架里会变得不可达）。
        var sameContent = _library.GetBookByFingerprint(book.Fingerprint);
        if (sameContent is not null && !string.IsNullOrEmpty(book.Fingerprint))
        {
            bool originalGone = !File.Exists(sameContent.FilePath) && !Directory.Exists(sameContent.FilePath);
            if (originalGone)
            {
                Log.Info($"源文件位置变化，复用已有记录: {sameContent.FilePath} -> {path}");
                sameContent.FilePath = path;
                sameContent.Title = book.Title;
                sameContent.FileSize = book.FileSize;
                sameContent.PageCount = book.PageCount;
                sameContent.Format = book.Format;
                sameContent.Error = null;
                _library.UpsertBook(sameContent);
                return true;
            }

            Log.Info($"跳过重复副本（与书架上已有条目内容相同，原文件仍在）: {path} -> {sameContent.FilePath}");
            return true; // 计为"已处理"，不算新增
        }

        string coverKey = CoverKey(book.Fingerprint);
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

    /// <summary>
    /// 由指纹派生安全的封面文件名。不能直接截取指纹原文：
    /// 文件夹书的指纹是 "dir:&lt;路径&gt;"，前 16 字符对所有文件夹都相同（互相覆盖），
    /// 且其中含 ':' 在 NTFS 上是非法文件名字符（导致封面永远生成失败）。
    /// </summary>
    private static string CoverKey(string fingerprint)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
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

        // 与 SaveComicBook 同一套判定：同路径=更新；内容相同但原路径已不在=移动/改名（接回）；
        // 内容相同且原路径仍在=副本（跳过，不改写原书 FilePath）。
        var samePath = _library.GetBookByPathNoCase(file);
        if (samePath is not null)
        {
            samePath.FilePath = file;
            samePath.Title = book.Title;
            samePath.FileSize = book.FileSize;
            samePath.PageCount = book.PageCount;
            samePath.ChapterCount = book.ChapterCount;
            samePath.Fingerprint = book.Fingerprint;
            samePath.Format = book.Format;
            samePath.Error = null;
            _library.UpsertBook(samePath);
            return true;
        }

        var sameContent = _library.GetBookByFingerprint(book.Fingerprint);
        if (sameContent is not null && !string.IsNullOrEmpty(book.Fingerprint))
        {
            bool originalGone = !File.Exists(sameContent.FilePath) && !Directory.Exists(sameContent.FilePath);
            if (originalGone)
            {
                Log.Info($"源文件位置变化，复用已有记录: {sameContent.FilePath} -> {file}");
                sameContent.FilePath = file;
                sameContent.Title = book.Title;
                sameContent.FileSize = book.FileSize;
                sameContent.PageCount = book.PageCount;
                sameContent.ChapterCount = book.ChapterCount;
                sameContent.Format = book.Format;
                sameContent.Error = null;
                _library.UpsertBook(sameContent);
                return true;
            }

            Log.Info($"跳过重复副本（与书架上已有条目内容相同，原文件仍在）: {file} -> {sameContent.FilePath}");
            return true;
        }

        string coverKey = CoverKey(book.Fingerprint);
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
