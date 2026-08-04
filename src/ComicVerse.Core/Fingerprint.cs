using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ComicVerse.Core;

/// <summary>
/// 快速文件指纹：文件大小 + 首 1MB 与末 1MB 的 SHA-256。
/// 避免对 &gt;2GB 文件全量哈希（对应 PRD Q7）。
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

        byte[] data = new byte[head.Length + tail.Length + 8];
        Buffer.BlockCopy(BitConverter.GetBytes(size), 0, data, 0, 8);
        Buffer.BlockCopy(head, 0, data, 8, head.Length);
        Buffer.BlockCopy(tail, 0, data, 8 + head.Length, tail.Length);
        byte[] hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[] ReadChunk(FileStream fs, long offset)
    {
        byte[] buf = new byte[ChunkBytes];
        fs.Position = offset;
        int read = fs.Read(buf, 0, ChunkBytes);
        if (read == buf.Length) return buf;
        var trimmed = new byte[read];
        Buffer.BlockCopy(buf, 0, trimmed, 0, read);
        return trimmed;
    }
}
