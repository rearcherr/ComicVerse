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
    private IComicSource _source;
    private readonly ImageCacheService _cache;
    private readonly SemaphoreSlim _decodeGate = new(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));
    private readonly Dictionary<int, Task<BitmapSource?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly int _prefetchCount;
    private readonly string _cacheKey;   // 每本书一个缓存命名空间，避免切换书籍时命中别的书的页
    private volatile bool _disposed;

    public int PageCount { get; }
    public string SourcePath { get; }

    public ComicImageLoader(IComicSource source, ImageCacheService cache, int prefetchCount = 4)
    {
        _source = source;
        _cache = cache;
        PageCount = source.PageCount;
        SourcePath = source.SourcePath;
        _cacheKey = source.SourcePath;
        _prefetchCount = Math.Clamp(prefetchCount, 0, 8);
    }

    public BitmapSource? GetCached(int index) => _cache.Get(_cacheKey, index);

    /// <summary>当前源（Dispose 后为 null）。用局部副本避免"闭包捕获字段 + 字段被置空"的竞态。</summary>
    private IComicSource? SourceRef => _source;

    /// <summary>仅读取图片尺寸（头部解码，用于条漫/双页布局）。</summary>
    public (int Width, int Height)? GetPageSize(int index)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        var source = _source;
        if (source is null) return null;
        try
        {
            return source.GetPageSize(index);
        }
        catch (Exception ex)
        {
            if (!_disposed) Log.Error($"读取第 {index} 页尺寸失败", ex);
            return null;
        }
    }

    public async Task<BitmapSource?> GetPageAsync(int index, CancellationToken ct = default)
    {
        if (_disposed || index < 0 || index >= PageCount) return null;
        var cached = _cache.Get(_cacheKey, index);
        if (cached is not null) return cached;

        int staleRetries = 0;
        while (true)
        {
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

            BitmapSource? result;
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

            // 等到的是被取消或解码失败的陈旧任务：移除后重新解码一次，避免跳页后误报加载失败
            lock (_lock)
            {
                if (_pending.TryGetValue(index, out var t) && ReferenceEquals(t, task))
                    _pending.Remove(index);
            }
            if (++staleRetries > 1)
                return null;
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
            if (_cache.Contains(_cacheKey, i)) continue;
            bool already;
            lock (_lock) already = _pending.ContainsKey(i);
            if (already) continue;
            lock (_lock)
            {
                // 预取不要挤占当前页请求：在途任务较多时暂停预取
                if (_pending.Count >= 3) return;
            }
            _ = GetPageAsync(i).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error("预取页失败 " + i, t.Exception);
            }, TaskScheduler.Default);
        }
    }

    private async Task<BitmapSource?> DecodeAsync(int index, CancellationToken ct)
    {
        // 先取局部引用再 await：Dispose() 会把 _source 置空，而 Task.Run 的闭包如果直接捕获字段，
        // 排队中的解码任务就会拿到 null 触发 NRE（表现为关窗瞬间日志刷一串"解码第 N 页失败"）。
        var source = _source;
        if (source is null || _disposed) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        bool acquired = false;
        try
        {
            await _decodeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            ct.ThrowIfCancellationRequested();
            if (_disposed) return null;   // 拿到许可时可能已被释放
            BitmapSource? img;
            if (source is IPageBitmapSource direct)
            {
                // PDF 等可直接出位图的源：跳过 PNG 编码/解码往返
                img = await Task.Run(() => direct.RenderPageBitmap(index), linked.Token).ConfigureAwait(false);
            }
            else
            {
                using var stream = await Task.Run(() => source.GetPageStream(index), linked.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                img = ImageHelper.DecodeFrozen(stream);
            }
            if (img is not null && !_disposed)
                _cache.Put(_cacheKey, index, img);
            return img;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 已释放时源被关闭属于预期，不再刷日志
            if (!_disposed) Log.Error($"解码第 {index} 页失败", ex);
            return null;
        }
        finally
        {
            if (acquired)
                _decodeGate.Release();
        }
    }

    /// <summary>当前书的缓存命名空间（ReaderWindow 关窗时用 App.SharedCache.Clear(key) 清理）。</summary>
    public string CacheKey => _cacheKey;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        Task<BitmapSource?>[] pending;
        lock (_lock) pending = _pending.Values.ToArray();
        var source = SourceRef;
        _source = null!;

        // 等在途解码结束后再释放源，避免渲染未完成时关闭 pdfium 文档导致卡死/崩溃。
        // 这里先同步等一小段：让已进入 RenderCore 的渲染完成，再交后台收尾剩余任务。
        Task.Run(async () =>
        {
            try
            {
                if (pending.Length > 0)
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // 被取消或超时：源对象的 Dispose 对未完成的渲染也是安全的（内部有锁），继续释放
            }
            try
            {
                if (source is not null) source.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("释放漫画源失败", ex);
            }
        });
    }
}
