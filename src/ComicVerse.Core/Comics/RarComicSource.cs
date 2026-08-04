using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ComicVerse.Core.Comics;

/// <summary>CBR/RAR 漫画源：基于 SharpCompress 流式读取。</summary>
public sealed class RarComicSource : IComicSource
{
    private readonly FileStream _fs;
    private readonly IArchive _archive;
    private readonly List<IArchiveEntry> _entries;

    public string SourcePath { get; }
    public int PageCount => _entries.Count;

    public RarComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _archive = RarArchive.OpenArchive(_fs, new ReaderOptions { LeaveStreamOpen = true });
            _entries = _archive.Entries
                .Where(e => !e.IsDirectory && e.Key is not null && ImageHelper.IsImageFile(e.Key))
                .OrderBy(e => e.Key, NaturalStringComparer.Instance)
                .ToList();
            if (_entries.Count == 0)
                throw new ComicSourceException("RAR 包内没有图片文件");
        }
        catch (ComicSourceException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Dispose();
            throw new ComicSourceException("无法打开 RAR/CBR 文件: " + ex.Message, ex);
        }
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        using var s = _entries[index].OpenEntryStream();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    public (int Width, int Height)? GetPageSize(int index)
    {
        using var stream = GetPageStream(index);
        return ImageHelper.GetDimensions(stream);
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _fs?.Dispose();
    }
}
