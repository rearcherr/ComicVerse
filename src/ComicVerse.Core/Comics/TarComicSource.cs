using System.Formats.Tar;
using System.IO;

namespace ComicVerse.Core.Comics;

/// <summary>
/// CBT/TAR 漫画源：读取 TAR 元数据建立随机访问索引，
/// 不复制图片数据；支持 USTAR、GNU LongName 及 PAX 局部扩展路径。
/// </summary>
public sealed class TarComicSource : IComicSource
{
    private readonly FileStream _fs;
    private readonly List<IndexedPage> _entries;
    private readonly object _readLock = new();

    public string SourcePath { get; }
    public int PageCount => _entries.Count;

    private sealed record IndexedPage(long DataOffset, string Name, long Size);
    // TarReader 会按元数据声明大小租用数组，必须在进入标准解析器前限制。
    private const long MaxMetadataBytes = 1L << 20;
    private const int MaxConsecutiveMetadata = 32;

    public TarComicSource(string path)
    {
        SourcePath = path;
        try
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _entries = BuildIndex(_fs);
            if (_entries.Count == 0)
                throw new ComicSourceException("TAR 包内没有图片文件");
        }
        catch (ComicSourceException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Dispose();
            throw new ComicSourceException("无法打开 TAR/CBT 文件: " + ex.Message, ex);
        }
    }

    private static List<IndexedPage> BuildIndex(FileStream fs)
    {
        var result = new List<IndexedPage>();
        using var reader = new TarReader(fs, leaveOpen: true);
        while (true)
        {
            // 不读取 entry.DataStream：seekable TarReader 返回时已定位到下一头，
            // 下一次 GetNextEntry 也只定位回同一处。预扫必须先于库内的元数据分配。
            ValidateMetadataAhead(fs);
            var entry = reader.GetNextEntry(copyData: false);
            if (entry is null) break;

            long dataOffset = entry.DataOffset;
            long size = entry.Length;
            if (dataOffset < 0 || dataOffset > fs.Length || size < 0 || size > fs.Length - dataOffset
                || fs.Position > fs.Length)
                throw new InvalidDataException("TAR 条目数据不完整或大小非法");

            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile
                && ImageHelper.IsImageFile(entry.Name))
            {
                result.Add(new IndexedPage(dataOffset, entry.Name, size));
            }
        }

        result.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return result;
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        // 共享 FileStream 并发定位/读取会互相覆盖 Position，导致读到损坏数据
        lock (_readLock)
        {
            var entry = _entries[index];
            _fs.Position = entry.DataOffset;
            // entry.Size 来自不可信的 TAR 头：不能直接当 MemoryStream 容量（会被构造成 GB 级预分配）
            PageReadGuard.EnsureSize(entry.Size);
            var ms = new MemoryStream((int)Math.Min(entry.Size, 1 << 20));
            try
            {
                byte[] buffer = new byte[1 << 16];
                long remaining = entry.Size;
                while (remaining > 0)
                {
                    int count = (int)Math.Min(buffer.Length, remaining);
                    _fs.ReadExactly(buffer.AsSpan(0, count));
                    ms.Write(buffer, 0, count);
                    remaining -= count;
                }
                ms.Position = 0;
                return ms;
            }
            catch
            {
                ms.Dispose();
                throw;
            }
        }
    }

    public (int Width, int Height)? GetPageSize(int index)
    {
        if (index < 0 || index >= _entries.Count) return null;
        lock (_readLock)
        {
            try
            {
                var entry = _entries[index];
                _fs.Position = entry.DataOffset;
                int probeBytes = (int)Math.Max(1, Math.Min(entry.Size, 384 * 1024));
                var fast = ImageHelper.GetDimensionsFromPrefix(_fs, out _, probeBytes);
                if (fast is not null) return fast;
            }
            catch
            {
                // 忽略，退回完整读取
            }
        }
        using var stream = GetPageStream(index);
        return ImageHelper.GetDimensions(stream);
    }

    public void Dispose()
    {
        _fs?.Dispose();
    }

    /// <summary>
    /// 只预扫连续元数据头，原样还原位置；普通图片载荷由 TarReader 以 seek 跳过。
    /// 元数据的 raw size 描述它自己的载荷，不能套用 PAX 对下一文件的 size 覆盖。
    /// </summary>
    private static void ValidateMetadataAhead(FileStream fs)
    {
        long start = fs.Position;
        try
        {
            Span<byte> header = stackalloc byte[512];
            int metadataCount = 0;
            while (fs.Position < fs.Length)
            {
                if (fs.Length - fs.Position < header.Length)
                    throw new InvalidDataException("TAR 头部不完整");
                fs.ReadExactly(header);
                char type = (char)header[156];
                if (type is not ('x' or 'g' or 'L' or 'K' or 'X'))
                    return;
                if (++metadataCount > MaxConsecutiveMetadata)
                    throw new InvalidDataException("TAR 连续元数据条目过多");

                long size = ReadMetadataSize(header.Slice(124, 12));
                if (size > MaxMetadataBytes)
                    throw new ComicSourceException("TAR 元数据超过 1 MB 上限，已拒绝以保护内存");
                long padded = checked(((size + 511) / 512) * 512);
                long next = checked(fs.Position + padded);
                if (next > fs.Length)
                    throw new InvalidDataException("TAR 元数据不完整");
                fs.Position = next;
            }
        }
        finally
        {
            fs.Position = start;
        }
    }

    private static long ReadMetadataSize(ReadOnlySpan<byte> field)
    {
        // GNU base-256 与传统八进制均只接受非负且可表示为 Int64 的大小。
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0)
                throw new InvalidDataException("TAR 元数据大小不能为负数");
            long binaryValue = field[0] & 0x7F;
            foreach (byte b in field[1..])
                binaryValue = checked(binaryValue * 256 + b);
            return binaryValue;
        }

        int first = 0, last = field.Length;
        while (first < last && field[first] is 0 or (byte)' ') first++;
        while (last > first && field[last - 1] is 0 or (byte)' ') last--;
        long value = 0;
        foreach (byte b in field[first..last])
        {
            if (b is < (byte)'0' or > (byte)'7')
                throw new InvalidDataException("TAR 元数据大小不是有效的八进制数");
            value = checked(value * 8 + b - (byte)'0');
        }
        return value;
    }
}
