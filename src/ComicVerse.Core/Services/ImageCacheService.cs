using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Core.Services;

/// <summary>
/// LRU 图片缓存：以估算字节数控制内存上限（默认 ≤400MB，可在设置中调整）。
/// </summary>
public sealed class ImageCacheService
{
    private readonly object _lock = new();
    // 键带来源标识：共享缓存被多本书共用，只用页号做键会让新书命中上一本书的画面
    private readonly Dictionary<(string Source, int Index), BitmapSource> _map = new();
    private readonly LinkedList<(string Source, int Index)> _lru = new();
    private long _bytes;
    private long _maxBytes = 400L * 1024 * 1024;

    public long MaxBytes
    {
        get { lock (_lock) return _maxBytes; }
        set
        {
            lock (_lock)
            {
                _maxBytes = Math.Max(32L * 1024 * 1024, value);
                EvictLocked();
            }
        }
    }

    public long EstimatedBytes
    {
        get { lock (_lock) return _bytes; }
    }

    public BitmapSource? Get(string sourceKey, int index)
    {
        var key = (sourceKey, index);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var img))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return img;
            }
            return null;
        }
    }

    public void Put(string sourceKey, int index, BitmapSource image)
    {
        if (image.IsFrozen == false) image.Freeze();
        var key = (sourceKey, index);
        lock (_lock)
        {
            long size = Estimate(image);
            // 单张解码结果本身就超预算（如 4000x6000 扫描图 = 96MB，上限 128MB 时一张就爆）：
            // 放进缓存会立刻被淘汰，还会把已缓存的相邻页全部挤掉，导致整体抖动。
            // 这种情况不入缓存，直接交给调用方使用（返回值不受影响）。
            if (size > _maxBytes)
            {
                if (_map.Remove(key, out var stale))
                {
                    _bytes -= Estimate(stale);
                    _lru.Remove(key);
                }
                if (_bytes < 0) _bytes = 0;
                return;
            }

            if (_map.TryGetValue(key, out var old))
            {
                _bytes -= Estimate(old);
                _lru.Remove(key);
            }
            _map[key] = image;
            _bytes += size;
            _lru.AddFirst(key);
            EvictLocked();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    /// <summary>只清掉某一本书的缓存（例如关闭阅读器时）。</summary>
    public void Clear(string sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey)) return;
        lock (_lock)
        {
            foreach (var key in _map.Keys.Where(k => k.Source == sourceKey).ToList())
            {
                if (_map.Remove(key, out var img))
                {
                    _bytes -= Estimate(img);
                    _lru.Remove(key);
                }
            }
            if (_bytes < 0) _bytes = 0;
        }
    }

    public bool Contains(string sourceKey, int index)
    {
        lock (_lock) return _map.ContainsKey((sourceKey, index));
    }

    private void EvictLocked()
    {
        // 原先的 `_lru.Count > 1` 会让"最后一张"永久驻留：即使它的体积远超预算
        // （用户把上限调到 128MB，实际占用仍可能到 GB 级）。改为允许清空。
        while (_bytes > _maxBytes && _lru.Count > 0)
        {
            var last = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_map.Remove(last, out var img))
                _bytes -= Estimate(img);
        }
        if (_bytes < 0) _bytes = 0;
    }

    private static long Estimate(BitmapSource bmp) =>
        Math.Max(64L, (long)bmp.PixelWidth * bmp.PixelHeight * 4);
}
