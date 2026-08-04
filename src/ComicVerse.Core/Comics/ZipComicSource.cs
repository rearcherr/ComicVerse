using System.IO;
using System.IO.Compression;

namespace ComicVerse.Core.Comics;

/// <summary>CBZ/ZIP 漫画源：仅读取中央目录，按需解压单页（流式）。</summary>
public sealed class ZipComicSource : IComicSource
{
    private readonly FileStream _fs;
    private readonly ZipArchive _zip;
    private readonly List<ZipArchiveEntry> _entries;

    public string SourcePath { get; }
    public int PageCount => _entries.Count;

    public ZipComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _zip = new ZipArchive(_fs, ZipArchiveMode.Read, leaveOpen: true);
            _entries = _zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !e.Name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .Where(e => ImageHelper.IsImageFile(e.FullName))
                .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
                .ToList();
            if (_entries.Count == 0)
                throw new ComicSourceException("压缩包内没有图片文件");
        }
        catch (ComicSourceException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Dispose();
            throw new ComicSourceException("无法打开 ZIP/CBZ 文件: " + ex.Message, ex);
        }
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        using var entryStream = _entries[index].Open();
        var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _zip?.Dispose();
        _fs?.Dispose();
    }
}
