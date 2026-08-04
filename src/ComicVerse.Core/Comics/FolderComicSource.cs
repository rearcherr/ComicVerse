using System.IO;

namespace ComicVerse.Core.Comics;

/// <summary>文件夹漫画源：按文件名自然排序，单页直接读文件。</summary>
public sealed class FolderComicSource : IComicSource
{
    private readonly List<string> _files;

    public string SourcePath { get; }
    public int PageCount => _files.Count;

    public FolderComicSource(string folder, IEnumerable<string>? explicitFiles = null)
    {
        SourcePath = folder;
        if (explicitFiles is not null)
        {
            _files = explicitFiles
                .Where(ImageHelper.IsImageFile)
                .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                .ToList();
        }
        else
        {
            _files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .Where(ImageHelper.IsImageFile)
                .OrderBy(f => Path.GetRelativePath(folder, f), NaturalStringComparer.Instance)
                .ToList();
        }
        if (_files.Count == 0)
            throw new ComicSourceException("文件夹内没有支持的图片文件");
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _files.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new MemoryStream(File.ReadAllBytes(_files[index]));
    }

    public (int Width, int Height)? GetPageSize(int index)
    {
        using var stream = GetPageStream(index);
        return ImageHelper.GetDimensions(stream);
    }

    public void Dispose()
    {
    }
}
