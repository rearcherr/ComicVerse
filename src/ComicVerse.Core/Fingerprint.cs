using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ComicVerse.Core;

/// <summary>
/// 快速文件指纹：文件大小 + 首/中/尾各 1MB 的 SHA-256。
/// 避免对 &gt;2GB 文件全量哈希（对应 PRD Q7），但必须覆盖中段：
/// 压缩包的内容在中间，只取首尾会把"同一套图重新打包/替换其中一话"误判为同一本书。
/// </summary>
public static class Fingerprint
{
    private const int ChunkBytes = 1024 * 1024;

    public static string Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = fs.Length;
        byte[] head = ReadChunk(fs, 0);
        byte[] tail = size > ChunkBytes ? ReadChunk(fs, size - ChunkBytes) : Array.Empty<byte>();
        // 文件足够大时补采样中段，覆盖 zip/rar 里"被替换的那一话"
        long midOffset = size / 2 - ChunkBytes / 2;
        byte[] mid = size > 2L * ChunkBytes && midOffset > 0 ? ReadChunk(fs, midOffset) : Array.Empty<byte>();

        byte[] data = new byte[head.Length + mid.Length + tail.Length + 8];
        Buffer.BlockCopy(BitConverter.GetBytes(size), 0, data, 0, 8);
        int pos = 8;
        Buffer.BlockCopy(head, 0, data, pos, head.Length);
        pos += head.Length;
        Buffer.BlockCopy(mid, 0, data, pos, mid.Length);
        pos += mid.Length;
        Buffer.BlockCopy(tail, 0, data, pos, tail.Length);
        byte[] hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[] ReadChunk(FileStream fs, long offset)
    {
        byte[] buf = new byte[ChunkBytes];
        fs.Position = offset;
        // 网络盘/被占用文件的单次 Read 可能短读，必须循环读满，否则同一文件两次导入会算出不同指纹
        int total = 0;
        while (total < buf.Length)
        {
            int read = fs.Read(buf, total, buf.Length - total);
            if (read <= 0) break;
            total += read;
        }
        if (total == buf.Length) return buf;
        var trimmed = new byte[total];
        Buffer.BlockCopy(buf, 0, trimmed, 0, total);
        return trimmed;
    }
}
