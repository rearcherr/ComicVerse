using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Windows.Media;
using ComicVerse.Core;
using ComicVerse.Core.Comics;

namespace ComicVerse.Tests;

public static partial class Program
{
    private static void TestTarPaxReliability(string work)
    {
        string root = Path.Combine(work, "tar-pax-reliability");
        Directory.CreateDirectory(root);
        string image2 = Path.Combine(root, "page2.png"), image10 = Path.Combine(root, "page10.png");
        TestData.MakePng(image2, 40, 60, Colors.DarkRed, "2");
        TestData.MakePng(image10, 48, 72, Colors.DarkBlue, "10");
        byte[] bytes2 = File.ReadAllBytes(image2), bytes10 = File.ReadAllBytes(image10);

        // Full Unicode path, write order deliberately opposite to natural reading order.
        string longDir = new string('a', 110) + "/中文漫画";
        string pax = Path.Combine(root, "long-path.cbt");
        WriteTarPaxArchive(pax, TarEntryFormat.Pax,
            (longDir + "/page10.png", bytes10), (longDir + "/page2.png", bytes2), ("zz.png", bytes10));
        CheckTarPaxPages(pax, bytes2, bytes10, bytes10);
        CheckTarPaxPages(pax, bytes2, bytes10, bytes10); // reopen and random reads remain identical

        string gnu = Path.Combine(root, "gnu-long-name.cbt");
        WriteTarPaxArchive(gnu, TarEntryFormat.Gnu,
            (longDir + "/page10.png", bytes10), (longDir + "/page2.png", bytes2));
        CheckTarPaxPages(gnu, bytes2, bytes10);
        string ustar = Path.Combine(root, "ustar-prefix.cbt");
        WriteTarPaxArchive(ustar, TarEntryFormat.Ustar,
            (new string('u', 110) + "/page10.png", bytes10), (new string('u', 110) + "/page2.png", bytes2));
        CheckTarPaxPages(ustar, bytes2, bytes10);

        // PAX path and decimal size override a raw name without extension and raw size=0.
        // The next ordinary entry must retain its own path, size and data offset.
        string overridden = Path.Combine(root, "local-path-size.cbt");
        byte[] attributes = TarPaxRecord("path", "a/章一/" + new string('中', 50) + "/page2.png")
            .Concat(TarPaxRecord("size", bytes2.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        using (var fs = File.Create(overridden))
        {
            WriteTarPaxRawEntry(fs, TarPaxHeader("PaxHeaders/page", 'x', attributes.Length), attributes);
            WriteTarPaxRawEntry(fs, TarPaxHeader("placeholder", '0', 0), bytes2);
            WriteTarPaxRawEntry(fs, TarPaxHeader("zz.png", '0', bytes10.Length), bytes10);
            fs.Write(new byte[1024]);
        }
        CheckTarPaxPages(overridden, bytes2, bytes10);

        // Each metadata kind must be rejected before the library can allocate from its size.
        foreach (char kind in new[] { 'x', 'g', 'L', 'K', 'X' })
        {
            string huge = Path.Combine(root, "huge-metadata-" + kind + ".cbt");
            File.WriteAllBytes(huge, TarPaxHeader("metadata", kind, 1L << 30));
            ExpectTarPaxRejected(huge, "元数据超过");
        }

        string truncatedMetadata = Path.Combine(root, "truncated-metadata.cbt");
        File.WriteAllBytes(truncatedMetadata, TarPaxHeader("metadata", 'x', 1024).Concat(new byte[3]).ToArray());
        ExpectTarPaxRejected(truncatedMetadata, "元数据不完整");
        string truncatedPage = Path.Combine(root, "truncated-page.cbt");
        File.WriteAllBytes(truncatedPage, TarPaxHeader("page.png", '0', 4096).Concat(bytes2).ToArray());
        ExpectTarPaxRejected(truncatedPage);

        string invalidRecord = Path.Combine(root, "invalid-pax-record.cbt");
        byte[] badRecord = Encoding.UTF8.GetBytes("999 path=page.png\n");
        using (var fs = File.Create(invalidRecord))
        {
            WriteTarPaxRawEntry(fs, TarPaxHeader("metadata", 'x', badRecord.Length), badRecord);
            WriteTarPaxRawEntry(fs, TarPaxHeader("page.png", '0', bytes2.Length), bytes2);
            fs.Write(new byte[1024]);
        }
        ExpectTarPaxRejected(invalidRecord);

        foreach (bool binary in new[] { false, true })
        {
            string invalidSize = Path.Combine(root, "invalid-metadata-size-" + binary + ".cbt");
            byte[] header = TarPaxHeader("metadata", 'x', 0);
            if (binary) header.AsSpan(124, 12).Fill(0xFF); // negative GNU base-256
            else header[124] = (byte)'9';
            TarPaxChecksum(header);
            File.WriteAllBytes(invalidSize, header);
            ExpectTarPaxRejected(invalidSize, binary ? "负数" : "八进制");
        }

        string metadataChain = Path.Combine(root, "metadata-chain.cbt");
        using (var fs = File.Create(metadataChain))
        {
            for (int i = 0; i < 33; i++) fs.Write(TarPaxHeader("metadata", 'g', 0));
            fs.Write(new byte[1024]);
        }
        ExpectTarPaxRejected(metadataChain, "过多");

        // The metadata budget does not lower the existing 256 MiB image limit.
        // A length-only file exercises rejection before page allocation or data reads.
        string hugePage = Path.Combine(root, "huge-page.cbt");
        long size = PageReadGuard.MaxPageBytes + 1;
        using (var fs = File.Create(hugePage))
        {
            fs.Write(TarPaxHeader("page.png", '0', size));
            fs.SetLength(512 + ((size + 511) / 512) * 512 + 1024);
        }
        using (var source = new TarComicSource(hugePage))
        {
            bool rejected = false;
            try { using var ignored = source.GetPageStream(0); }
            catch (ComicSourceException ex) { rejected = ex.Message.Contains("条目过大", StringComparison.Ordinal); }
            Assert(rejected, "TAR 超大图片没有在分配前被 PageReadGuard 拒绝");
        }
        File.Delete(hugePage);

        string empty = Path.Combine(root, "no-images.cbt");
        WriteTarPaxArchive(empty, TarEntryFormat.Pax, ("notes.txt", Encoding.UTF8.GetBytes("plain text")));
        ExpectTarPaxRejected(empty, "没有图片");
    }

    private static void CheckTarPaxPages(string path, params byte[][] expected)
    {
        using var source = new TarComicSource(path);
        Assert(source.PageCount == expected.Length, $"TAR 页数错误 {Path.GetFileName(path)}: {source.PageCount}/{expected.Length}");
        foreach (int index in Enumerable.Range(0, expected.Length).Reverse().Concat(Enumerable.Range(0, expected.Length)))
        {
            using var stream = source.GetPageStream(index);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert(bytes.ToArray().SequenceEqual(expected[index]), $"TAR 自然排序或随机偏移错误: {Path.GetFileName(path)} page={index}");
            bytes.Position = 0;
            var image = ImageHelper.DecodeFrozen(bytes);
            Assert(image is not null, $"TAR 正常页无法解码: {Path.GetFileName(path)} page={index}");
            var dimensions = source.GetPageSize(index);
            Assert(dimensions?.Width == image!.PixelWidth && dimensions?.Height == image.PixelHeight, "TAR 页尺寸与实际位图不一致");
        }
        Task.WhenAll(Enumerable.Range(0, 12).Select(iteration => Task.Run(() =>
        {
            int index = iteration % expected.Length;
            using var stream = source.GetPageStream(index);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert(bytes.ToArray().SequenceEqual(expected[index]), "TAR 并发随机读取串页");
        }))).GetAwaiter().GetResult();
    }

    private static void WriteTarPaxArchive(string path, TarEntryFormat format, params (string Name, byte[] Bytes)[] pages)
    {
        using var fs = File.Create(path);
        using var writer = new TarWriter(fs, format, leaveOpen: true);
        foreach (var (name, bytes) in pages)
        {
            TarEntry entry = format switch
            {
                TarEntryFormat.Pax => new PaxTarEntry(TarEntryType.RegularFile, name),
                TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, name),
                _ => new UstarTarEntry(TarEntryType.RegularFile, name)
            };
            using var data = new MemoryStream(bytes, writable: false);
            entry.DataStream = data;
            writer.WriteEntry(entry);
        }
    }

    private static void ExpectTarPaxRejected(string path, string? message = null)
    {
        bool rejected = false;
        try { using var ignored = new TarComicSource(path); }
        catch (ComicSourceException ex)
        {
            rejected = true;
            if (message is not null)
                Assert(ex.Message.Contains(message, StringComparison.Ordinal), $"TAR 拒绝原因不符 {Path.GetFileName(path)}: {ex.Message}");
        }
        Assert(rejected, "TAR 非法归档未拒绝: " + Path.GetFileName(path));
    }

    private static byte[] TarPaxRecord(string key, string value)
    {
        string content = key + "=" + value + "\n";
        int contentBytes = Encoding.UTF8.GetByteCount(content), length = contentBytes + 2;
        while (true)
        {
            string digits = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int actual = digits.Length + 1 + contentBytes;
            if (actual == length) return Encoding.UTF8.GetBytes(digits + " " + content);
            length = actual;
        }
    }

    private static byte[] TarPaxHeader(string name, char type, long size)
    {
        var header = new byte[512];
        Encoding.UTF8.GetBytes(name).AsSpan(0, Math.Min(Encoding.UTF8.GetByteCount(name), 100)).CopyTo(header);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        header[156] = (byte)type;
        Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(header, 257);
        TarPaxChecksum(header);
        return header;
    }

    private static void TarPaxChecksum(byte[] header)
    {
        header.AsSpan(148, 8).Fill((byte)' ');
        int checksum = header.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
    }

    private static void WriteTarPaxRawEntry(Stream stream, byte[] header, byte[] data)
    {
        stream.Write(header);
        stream.Write(data);
        stream.Write(new byte[(512 - data.Length % 512) % 512]);
    }
}
