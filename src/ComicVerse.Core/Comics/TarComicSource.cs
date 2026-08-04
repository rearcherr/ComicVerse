using System.IO;
using System.Text;

namespace ComicVerse.Core.Comics;

/// <summary>
/// CBT/TAR 漫画源：手工遍历 TAR 头（512 字节/块），只读取头部建立索引，
/// 不读取整个文件数据，避免大包全量解压（支持 GNU LongName）。
/// </summary>
public sealed class TarComicSource : IComicSource
{
    private readonly FileStream _fs;
    private readonly List<TarEntry> _entries;

    public string SourcePath { get; }
    public int PageCount => _entries.Count;

    private sealed record TarEntry(long HeaderOffset, string Name, long Size);

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

    private static List<TarEntry> BuildIndex(FileStream fs)
    {
        var result = new List<TarEntry>();
        byte[] header = new byte[512];
        long offset = 0;
        long length = fs.Length;
        string pendingLongName = "";

        while (offset + 512 <= length)
        {
            fs.Position = offset;
            if (fs.Read(header, 0, 512) != 512)
                break;

            bool allZero = true;
            foreach (byte b in header)
            {
                if (b != 0) { allZero = false; break; }
            }
            if (allZero)
                break;

            string name = ReadString(header, 0, 100);
            long size = ParseOctal(header.AsSpan(124, 12));
            char type = (char)header[156];
            string prefix = ReadString(header, 345, 155);

            long dataOffset = offset + 512;
            long padded = ((size + 511) / 512) * 512;

            if (type == 'L') // GNU LongName：下一块是真正文件名
            {
                pendingLongName = ReadLongName(fs, dataOffset, size);
                offset = dataOffset + padded;
                continue;
            }
            if (type is 'K' or 'x' or 'g' or 'X') // 跳过 longlink / pax
            {
                offset = dataOffset + padded;
                continue;
            }

            string fullName = pendingLongName;
            pendingLongName = "";
            if (string.IsNullOrEmpty(fullName))
                fullName = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;

            bool isRegular = type is '\0' or '0' or '7' or ' ';
            if (isRegular && size >= 0 && !string.IsNullOrEmpty(fullName) && ImageHelper.IsImageFile(fullName))
            {
                result.Add(new TarEntry(offset, fullName, size));
            }

            offset = dataOffset + padded;
        }

        result.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return result;
    }

    public Stream GetPageStream(int index)
    {
        if (index < 0 || index >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var entry = _entries[index];
        _fs.Position = entry.HeaderOffset + 512;
        var ms = new MemoryStream((int)Math.Min(entry.Size, int.MaxValue));
        byte[] buffer = new byte[1 << 16];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int read = _fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
            remaining -= read;
        }
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _fs?.Dispose();
    }

    private static string ReadString(byte[] buf, int offset, int maxLen)
    {
        int len = 0;
        while (len < maxLen && offset + len < buf.Length && buf[offset + len] != 0)
            len++;
        return Encoding.UTF8.GetString(buf, offset, len).Trim();
    }

    private static string ReadLongName(FileStream fs, long offset, long size)
    {
        fs.Position = offset;
        var buf = new byte[Math.Min(size, 4096)];
        int read = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, read).TrimEnd('\0');
    }

    private static long ParseOctal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (byte b in field)
        {
            if (b == 0 || b == ' ' || b == 0x7F) continue;
            if (b is < (byte)'0' or > (byte)'7') continue;
            value = value * 8 + (b - (byte)'0');
        }
        return value;
    }
}
