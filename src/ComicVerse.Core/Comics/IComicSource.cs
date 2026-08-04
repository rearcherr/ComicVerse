using System.IO;

namespace ComicVerse.Core.Comics;

/// <summary>
/// 漫画数据源抽象：按页号流式读取图片，不一次性解压整个文件。
/// </summary>
public interface IComicSource : IDisposable
{
    string SourcePath { get; }
    int PageCount { get; }

    /// <summary>返回第 index 页（0 起）图片字节流（内存流，调用方负责解码与释放）。</summary>
    Stream GetPageStream(int index);
}

public sealed class ComicSourceException : Exception
{
    public ComicSourceException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
