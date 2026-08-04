using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicVerse.Core.Services;

/// <summary>
/// LRU 图片缓存：以估算字节数控制内存上限（默认 ≤400MB，可在设置中调整）。
/// </summary>
public sealed class ImageCacheService
{
    private readonly object _lock = new();
    private readonly Dictionary<int, BitmapSource> _map = new();
    private readonly LinkedList<int> _lru = new();
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

    public BitmapSource? Get(int index)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(index, out var img))
            {
                _lru.Remove(index);
                _lru.AddFirst(index);
                return img;
            }
            return null;
        }
    }

    public void Put(int index, BitmapSource image)
    {
        if (image.IsFrozen == false) image.Freeze();
        lock (_lock)
        {
            if (_map.TryGetValue(index, out var old))
            {
                _bytes -= Estimate(old);
                _lru.Remove(index);
            }
            _map[index] = image;
            _bytes += Estimate(image);
            _lru.AddFirst(index);
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

    public bool Contains(int index)
    {
        lock (_lock) return _map.ContainsKey(index);
    }

    private void EvictLocked()
    {
        while (_bytes > _maxBytes && _lru.Count > 1)
        {
            int last = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_map.Remove(last, out var img))
                _bytes -= Estimate(img);
        }
    }

    private static long Estimate(BitmapSource bmp) =>
        Math.Max(64L, (long)bmp.PixelWidth * bmp.PixelHeight * 4);
}
