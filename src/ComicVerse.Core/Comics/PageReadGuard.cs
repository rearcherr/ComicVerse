using System.IO;

namespace ComicVerse.Core.Comics;

/// <summary>
/// 压缩包条目读取的体积上限。归档头里的 size 字段完全不可信（可被构造为 2GB+ 甚至负数），
/// 直接按声明值预分配内存会让一个几 KB 的文件瞬间吃掉 GB 级内存（解压炸弹）。
/// </summary>
public static class PageReadGuard
{
    /// <summary>单页允许的最大原始字节数。</summary>
    public const long MaxPageBytes = 256L * 1024 * 1024;

    /// <summary>读取时允许的预分配上限，避免按不可信的 size 一次申请巨量内存。</summary>
    private const int PreallocateLimit = 1 << 20; // 1MB

    /// <summary>校验声明的条目大小，超出上限则拒绝。</summary>
    public static void EnsureSize(long declaredSize)
    {
        if (declaredSize < 0)
            throw new ComicSourceException("压缩包条目大小非法，可能文件已损坏");
        if (declaredSize > MaxPageBytes)
            throw new ComicSourceException($"压缩包条目过大（{declaredSize / 1024 / 1024} MB），已拒绝以保护内存");
    }

    /// <summary>
    /// 把 <paramref name="declaredSize"/> 字节以内的流读进内存。预分配量被限制在 1MB 以内，
    /// 并在拷贝过程中二次校验上限（防御头部谎报）。
    /// </summary>
    public static MemoryStream ReadToEnd(Stream stream, long declaredSize)
    {
        EnsureSize(declaredSize);
        var ms = new MemoryStream((int)Math.Min(declaredSize, PreallocateLimit));
        byte[] buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxPageBytes)
                throw new ComicSourceException($"压缩包条目解压后超过 {MaxPageBytes / 1024 / 1024} MB，已中止以保护内存");
            ms.Write(buffer, 0, read);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>无长度信息的流（如压缩流）使用：边读边校验上限。</summary>
    public static MemoryStream ReadToEnd(Stream stream) => ReadToEnd(stream, stream.CanSeek ? stream.Length : MaxPageBytes);
}
