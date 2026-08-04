using System.IO;

namespace ComicVerse.Core.Comics;

public static class ComicSourceFactory
{
    public static IComicSource Create(string path)
    {
        if (Directory.Exists(path))
        {
            return new FolderComicSource(path);
        }

        if (!File.Exists(path))
        {
            throw new ComicSourceException("文件不存在: " + path);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".cbz":
            case ".zip":
                return new ZipComicSource(path);

            case ".cbr":
            case ".rar":
                // 部分扩展名错误但实为 ZIP 的文件做降级尝试
                return TryFallback(() => new RarComicSource(path), () => new ZipComicSource(path), path);

            case ".cbt":
            case ".tar":
                return TryFallback(() => new TarComicSource(path), () => new ZipComicSource(path), path);

            case ".cb7":
            case ".7z":
                return new SevenZipComicSource(path);

            case ".pdf":
                return new PdfComicSource(path);

            default:
                if (ImageHelper.IsImageFile(path))
                {
                    return new FolderComicSource(Path.GetDirectoryName(path)!, new[] { path });
                }
                throw new ComicSourceException("不支持的文件格式: " + Path.GetExtension(path));
        }
    }

    private static IComicSource TryFallback(Func<IComicSource> primary, Func<IComicSource> fallback, string path)
    {
        Exception? first = null;
        try
        {
            var source = primary();
            if (source.PageCount > 0) return source;
            source.Dispose();
        }
        catch (Exception ex)
        {
            first = ex;
        }

        try
        {
            var source = fallback();
            if (source.PageCount > 0) return source;
            source.Dispose();
        }
        catch (Exception ex)
        {
            throw new ComicSourceException("无法读取漫画文件: " + Path.GetFileName(path), first ?? ex);
        }

        throw new ComicSourceException("压缩包内未找到图片: " + Path.GetFileName(path));
    }
}
