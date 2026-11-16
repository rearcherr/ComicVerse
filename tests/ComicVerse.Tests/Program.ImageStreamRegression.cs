using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.Core;
using ComicVerse.Core.Comics;

namespace ComicVerse.Tests;

public static partial class Program
{
    private static byte[] ImageDimensionPng()
    {
        var image = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[]
            {
                0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
                255, 255, 255, 255, 0, 0, 0, 255, 0, 255, 255, 255
            }, 12);
        return ImageHelper.EncodePng(image);
    }

    private static void TestImageDimensionCompressedStreams(string work)
    {
        byte[] valid = ImageDimensionPng();
        // PNG signature followed by a broken IHDR: reproduces the failed prefix/fallback path.
        byte[] invalid = new byte[4096];
        valid.AsSpan(0, 8).CopyTo(invalid);
        string path = Path.Combine(work, "image-stream-regression.cbz");
        using (var file = File.Create(path))
        using (var writer = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in new[] { ("01-valid.png", valid), ("02-invalid.png", invalid) })
            {
                using var entry = writer.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(bytes);
            }
        }

        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var entry in archive.Entries)
            {
                using var stream = new ImageDimensionReadStream(entry.Open());
                Assert(!stream.CanSeek, "ZIP 测试必须经过非 seek 流");
                var dimensions = ImageHelper.GetDimensions(stream);
                Assert(stream.AsyncReads == 0, "取尺寸向原始压缩流发起了异步读取");
                Assert(stream.SyncReads > 0 && stream.ReadToEof && stream.BytesRead == entry.Length,
                    "非 seek 流未在返回前同步读完（包括短读）");
                Assert(!stream.WrongThreadRead, "原始压缩流被其他线程读取");
                Assert(!stream.IsDisposed && stream.CanRead, "取尺寸释放了调用者的压缩流");
                Assert(stream.ReadByte() == -1, "返回后调用者仍应能访问原始流");
                if (entry.Name == "01-valid.png")
                    Assert(dimensions == (3, 2), "有效非 seek PNG 未返回正确尺寸");
                else
                    Assert(dimensions is null, "损坏非 seek PNG 应返回 null");
            }
            // Disposing each caller-owned entry must leave the archive usable.
            using var reopened = archive.Entries[0].Open();
            Assert(reopened.ReadByte() == valid[0], "调用者释放条目后压缩包不可重读");
        }

        // A synchronous decompression error keeps the same null/ownership contract.
        using (var compressed = new MemoryStream(new byte[] { 0x07 })) // reserved DEFLATE block type
        using (var stream = new ImageDimensionReadStream(new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true)))
        {
            Assert(ImageHelper.GetDimensions(stream) is null, "损坏 DEFLATE 流应返回 null");
            Assert(stream.SyncReads > 0 && stream.AsyncReads == 0 && !stream.WrongThreadRead,
                "解压错误必须来自调用线程的同步读取");
            Assert(!stream.IsDisposed && stream.CanRead, "解压失败释放了调用者流");
        }

        // Exercise the actual ZIP prefix + full fallback and immediate entry disposal.
        using var source = new ZipComicSource(path);
        Assert(source.GetPageSize(0) == (3, 2), "ZIP 正常 prefix 尺寸回归");
        Assert(source.GetPageSize(1) is null, "ZIP 坏 PNG fallback 应返回 null");
        Assert(source.GetPageSize(0) == (3, 2), "坏 PNG fallback 后 ZIP 正常页不可读");
    }

    private static void TestImageDimensionReadLimit()
    {
        // Generate bytes on demand instead of keeping a second 256 MiB fixture in memory.
        // A valid PNG at the front ensures an unbounded/header-only decode cannot pass.
        using var payload = new ImageDimensionPaddedStream(ImageDimensionPng(), PageReadGuard.MaxPageBytes + 2 * 65536);
        using var stream = new ImageDimensionReadStream(payload, maxRead: int.MaxValue);
        Assert(ImageHelper.GetDimensions(stream) is null, "超出 PageReadGuard 的非 seek 图片应返回 null");
        Assert(stream.AsyncReads == 0 && !stream.WrongThreadRead, "上限检查使用了异步读取");
        Assert(stream.BytesRead > PageReadGuard.MaxPageBytes && stream.BytesRead <= PageReadGuard.MaxPageBytes + 65536,
            $"未在 256 MiB 上限后的首个缓冲区停止，实际读取 {stream.BytesRead} 字节");
        Assert(!stream.ReadToEof, "限流必须在耗尽超大输入之前停止");
        Assert(!stream.IsDisposed && stream.CanRead && stream.ReadByte() == 0, "限流失败释放了调用者流");
    }

    private static void TestImageDimensionPrefixReadLimit()
    {
        const int prefixLimit = 384 * 1024;
        using var payload = new ImageDimensionPaddedStream(ImageDimensionPng(), prefixLimit + 65536);
        using var stream = new ImageDimensionReadStream(payload);
        Assert(ImageHelper.GetDimensionsFromPrefix(stream, out int bytesRead) == (3, 2), "有效 PNG prefix 尺寸错误");
        Assert(bytesRead == prefixLimit && stream.BytesRead == prefixLimit && !stream.ReadToEof,
            "正常 prefix 路径读取了头部预算之外的数据");
        Assert(stream.AsyncReads == 0 && !stream.WrongThreadRead, "prefix 路径异步读取原始流");
        Assert(!stream.IsDisposed && stream.CanRead && stream.ReadByte() == 0, "prefix 路径释放了调用者流");
    }

    /// <summary>Reject async reads synchronously so the old WPF path fails deterministically without orphan tasks.</summary>
    private sealed class ImageDimensionReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private readonly int _maxRead;
        public int SyncReads { get; private set; }
        public int AsyncReads { get; private set; }
        public long BytesRead { get; private set; }
        public bool ReadToEof { get; private set; }
        public bool WrongThreadRead { get; private set; }
        public bool IsDisposed { get; private set; }

        public ImageDimensionReadStream(Stream inner, int maxRead = 97)
        {
            Assert(!inner.CanSeek, "原始测试流必须不可 seek");
            _inner = inner;
            _maxRead = maxRead;
        }

        public override bool CanRead => !IsDisposed && _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            SyncReads++;
            if (Environment.CurrentManagedThreadId != _ownerThread)
            {
                WrongThreadRead = true;
                throw new InvalidOperationException("非调用线程读取原始流");
            }
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            int read = _inner.Read(buffer[..Math.Min(buffer.Length, _maxRead)]);
            BytesRead += read;
            if (read == 0 && buffer.Length > 0) ReadToEof = true;
            return read;
        }

        private NotSupportedException RejectAsyncRead()
        {
            AsyncReads++;
            return new NotSupportedException("尺寸读取不应异步访问原始流");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw RejectAsyncRead();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw RejectAsyncRead();
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
            => throw RejectAsyncRead();
        public override int EndRead(IAsyncResult asyncResult) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A non-seek PNG followed by generated padding, used for real byte-budget checks.</summary>
    private sealed class ImageDimensionPaddedStream(byte[] prefix, long length) : Stream
    {
        private long _bytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - _bytesRead);
            buffer[..count].Clear();
            if (_bytesRead < prefix.Length)
                prefix.AsSpan((int)_bytesRead, Math.Min(count, prefix.Length - (int)_bytesRead)).CopyTo(buffer);
            _bytesRead += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
