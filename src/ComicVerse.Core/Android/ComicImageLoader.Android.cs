using Android.Graphics;
using ComicVerse.Core.Comics;

namespace ComicVerse.Core.Services;

/// <summary>漫画页解码器（安卓）：后台按需解码 + LRU 缓存 + 相邻页预取。</summary>
public sealed class ComicImageLoader : IDisposable
{
    private IComicSource _source;
    private readonly ImageCacheService _cache;
    private readonly SemaphoreSlim _decodeGate =
        new(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));
    private readonly Dictionary<int, Task<Bitmap?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
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

    public Bitmap? GetCached(int index) => _cache.Get(index);

    public (int Width, int Height)? GetPageSize(int index)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        try
        {
            return _source.GetPageSize(index);
        }
        catch (Exception ex)
        {
            Log.Error($"读取第 {index} 页尺寸失败", ex);
            return null;
        }
    }

    public async Task<Bitmap?> GetPageAsync(int index, CancellationToken ct = default)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        var cached = _cache.Get(index);
        if (cached is not null) return cached;

        int staleRetries = 0;
        while (true)
        {
            Task<Bitmap?> task;
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

            Bitmap? result;
            try
            {
                result = alreadyPending
                    ? await task.WaitAsync(ct).ConfigureAwait(false)
                    : await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return null;
                result = null;
            }

            if (result is not null)
            {
                lock (_lock)
                {
                    if (_pending.TryGetValue(index, out var t) && ReferenceEquals(t, task))
                        _pending.Remove(index);
                }
                return result;
            }

            lock (_lock)
            {
                if (_pending.TryGetValue(index, out var t) && ReferenceEquals(t, task))
                    _pending.Remove(index);
            }
            if (++staleRetries > 1)
                return null;
        }
    }

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
            lock (_lock)
            {
                if (_pending.Count >= 3) return;
            }
            _ = GetPageAsync(i).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error("预取页失败 " + i, t.Exception);
            }, TaskScheduler.Default);
        }
    }

    private async Task<Bitmap?> DecodeAsync(int index, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        bool acquired = false;
        try
        {
            await _decodeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            ct.ThrowIfCancellationRequested();
            Bitmap? img = await Task.Run(() =>
            {
                using var stream = _source.GetPageStream(index);
                return ImageHelper.Decode(stream);
            }, linked.Token).ConfigureAwait(false);
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
            if (acquired)
                _decodeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        Task<Bitmap?>[] pending;
        lock (_lock) pending = _pending.Values.ToArray();
        var source = _source;
        _source = null!;

        Task.Run(async () =>
        {
            try
            {
                if (pending.Length > 0)
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
            try
            {
                source.Dispose();
            }
            catch
            {
            }
        });
    }
}
