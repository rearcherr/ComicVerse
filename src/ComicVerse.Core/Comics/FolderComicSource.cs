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
        if (index < 0 || index >= _files.Count) return null;
        try
        {
            // 大图只读文件头部即可得到尺寸，避免整文件读入内存
            using var fs = new FileStream(_files[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var fast = ImageHelper.GetDimensionsFromPrefix(fs, out _);
            if (fast is not null) return fast;
        }
        catch
        {
            // 忽略，退回完整读取
        }
        using var stream = GetPageStream(index);
        return ImageHelper.GetDimensions(stream);
    }

    public void Dispose()
    {
    }
}
