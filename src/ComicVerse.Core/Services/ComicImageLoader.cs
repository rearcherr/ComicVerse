using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.Core.Comics;

namespace ComicVerse.Core.Services;

/// <summary>
/// 漫画页解码器：后台按需解码 + LRU 缓存 + 相邻页预取（US-06/US-07）。
/// </summary>
public sealed class ComicImageLoader : IDisposable
{
    private readonly IComicSource _source;
    private readonly ImageCacheService _cache;
    private readonly SemaphoreSlim _decodeGate = new(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));
    private readonly Dictionary<int, Task<BitmapSource?>> _pending = new();
    private readonly object _lock = new();
    private readonly int _prefetchCount;
    private volatile bool _disposed;

    public int PageCount { get; }
    public string SourcePath { get; }

    public ComicImageLoader(IComicSource source, ImageCacheService cache, int prefetchCount = 4)
    {
        _source = source;
        _cache = cache;
        PageCount = source.PageCount;
        SourcePath = source.SourcePath;
        _prefetchCount = Math.Clamp(prefetchCount, 0, 8);
    }

    public BitmapSource? GetCached(int index) => _cache.Get(index);

    /// <summary>仅读取图片尺寸（头部解码，用于条漫/双页布局）。</summary>
    public (int Width, int Height)? GetPageSize(int index)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        try
        {
            using var stream = _source.GetPageStream(index);
            return ImageHelper.GetDimensions(stream);
        }
        catch (Exception ex)
        {
            Log.Error($"读取第 {index} 页尺寸失败", ex);
            return null;
        }
    }

    public async Task<BitmapSource?> GetPageAsync(int index, CancellationToken ct = default)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        var cached = _cache.Get(index);
        if (cached is not null) return cached;

        Task<BitmapSource?> task;
        bool alreadyPending;
        lock (_lock)
        {
            if (_pending.TryGetValue(index, out task!))
            {
                alreadyPending = true;
            }
            else
            {
                alreadyPending = false;
                task = DecodeAsync(index, ct);
                _pending[index] = task;
            }
        }

        if (alreadyPending)
            return await task.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await task.ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_lock) _pending.Remove(index);
        }
    }

    /// <summary>预取 center 前后若干页（后台执行，不阻塞）。</summary>
    public void Prefetch(int center)
    {
        if (_prefetchCount <= 0) return;
        int start = Math.Max(0, center - 1);
        int end = Math.Min(PageCount - 1, center + _prefetchCount);
        for (int i = start; i <= end; i++)
        {
            if (_cache.Contains(i)) continue;
            bool already;
            lock (_lock) already = _pending.ContainsKey(i);
            if (already) continue;
            _ = GetPageAsync(i).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error("预取页失败 " + i, t.Exception);
            }, TaskScheduler.Default);
        }
    }

    private async Task<BitmapSource?> DecodeAsync(int index, CancellationToken ct)
    {
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = await Task.Run(() => _source.GetPageStream(index), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var img = ImageHelper.DecodeFrozen(stream);
            if (img is not null)
                _cache.Put(index, img);
            return img;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"解码第 {index} 页失败", ex);
            return null;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _source.Dispose();
        _decodeGate.Dispose();
    }
}
