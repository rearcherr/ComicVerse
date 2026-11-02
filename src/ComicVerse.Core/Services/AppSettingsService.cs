using System.Globalization;
using System.Text.Json;
using ComicVerse.Core.Models;

namespace ComicVerse.Core.Services;

/// <summary>
/// 应用设置：主题、缓存、阅读偏好等，存于 SQLite settings 表。
/// 构造时一次性把 settings 表读进内存字典：读取从此不再碰 SQLite（都是 UI 线程同步调用，
/// 原先每读一个设置就是一次数据库往返）。写入先更新会话缓存再落库，失败保留待保存标记，
/// 相同值可重试；数据库写入串行执行，但读取缓存不等待数据库 I/O。
/// </summary>
public sealed class AppSettingsService
{
    private readonly LibraryService _library;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly object _writeGate = new();

    public AppSettingsService(LibraryService library)
    {
        _library = library;
        Reload();
    }

    /// <summary>重新从数据库读取全部设置（外部直接改 settings 表后调用）。</summary>
    public void Reload()
    {
        lock (_writeGate)
        {
            try
            {
                var all = _library.GetSettings();
                lock (_lock)
                {
                    // 失败的会话值尚未落盘，不能在 Reload 时被旧数据库值抹掉。
                    var pending = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string key in _dirtyKeys)
                        if (_cache.TryGetValue(key, out string? value)) pending[key] = value;
                    _cache.Clear();
                    foreach (var kv in all) _cache[kv.Key] = kv.Value;
                    foreach (var kv in pending) _cache[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取设置失败，保留会话值或使用默认值", ex);
            }
        }
    }

    public string Theme
    {
        get => Get("theme", "dark");
        set => Set("theme", value);
    }

    public long CacheLimitMb
    {
        get => long.TryParse(Get("cache_limit_mb", "400"), out var v) ? Math.Clamp(v, 128, 2048) : 400;
        set => Set("cache_limit_mb", value.ToString(CultureInfo.InvariantCulture));
    }

    public int PrefetchPages
    {
        get => int.TryParse(Get("prefetch_pages", "4"), out var v) ? Math.Clamp(v, 0, 8) : 4;
        set => Set("prefetch_pages", value.ToString(CultureInfo.InvariantCulture));
    }

    public bool LowPerformanceMode
    {
        get => Get("low_perf", "0") == "1";
        set => Set("low_perf", value ? "1" : "0");
    }

    public bool MangaRightToLeft
    {
        get => Get("manga_rtl", "0") == "1";
        set => Set("manga_rtl", value ? "1" : "0");
    }

    /// <summary>漫画打开时的默认阅读方式：paged / webtoon / double。</summary>
    public string DefaultComicMode
    {
        get
        {
            string v = Get("default_comic_mode", "webtoon");
            return v is "paged" or "double" ? v : "webtoon";
        }
        set => Set("default_comic_mode", value is "paged" or "double" ? value : "webtoon");
    }

    public bool AutoHideBars
    {
        get => Get("auto_hide_bars", "0") == "1";
        set => Set("auto_hide_bars", value ? "1" : "0");
    }

    /// <summary>小说自动滚动速度（20–160）。</summary>
    public int AutoScrollSpeed
    {
        get => int.TryParse(Get("auto_scroll_speed", "60"), out var v) ? Math.Clamp(v, 20, 160) : 60;
        set => Set("auto_scroll_speed", Math.Clamp(value, 20, 160).ToString(CultureInfo.InvariantCulture));
    }

    public string LibraryView
    {
        get => Get("library_view", "grid");
        set => Set("library_view", value);
    }

    public NovelViewSettings NovelSettings
    {
        get
        {
            string? json = Get("novel_settings", "");
            if (string.IsNullOrEmpty(json)) return new NovelViewSettings();
            try
            {
                return JsonSerializer.Deserialize<NovelViewSettings>(json) ?? new NovelViewSettings();
            }
            catch
            {
                return new NovelViewSettings();
            }
        }
        set => Set("novel_settings", JsonSerializer.Serialize(value));
    }

    /// <summary>按书恢复小说排版；没有有效记录时使用全局默认，返回值可独立修改。</summary>
    public NovelViewSettings GetNovelSettingsForBook(long bookId)
    {
        if (bookId <= 0) return NovelSettings.Clone();
        string json = Get(NovelSettingsBookKey(bookId), "");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<NovelViewSettings>(json);
                if (settings is not null) return settings;
            }
            catch (JsonException)
            {
                // 损坏的单本设置不影响打开，也不修改用户的全局默认值。
            }
        }
        return NovelSettings.Clone();
    }

    /// <summary>只保存该书的完整排版，不改变其它书或全局默认。</summary>
    /// <returns>值已成功持久化时为 true；数据库写入失败时为 false，保留会话值供之后重试。</returns>
    public bool SaveNovelSettingsForBook(long bookId, NovelViewSettings settings)
    {
        if (bookId <= 0) throw new ArgumentOutOfRangeException(nameof(bookId));
        ArgumentNullException.ThrowIfNull(settings);
        return Set(NovelSettingsBookKey(bookId), JsonSerializer.Serialize(settings));
    }

    private static string NovelSettingsBookKey(long bookId)
        => "novel_settings_book_" + bookId.ToString(CultureInfo.InvariantCulture);

    public string LastImportDir
    {
        get => Get("last_import_dir", "");
        set => Set("last_import_dir", value);
    }

    private string Get(string key, string fallback)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var v) ? v : fallback;
        }
    }

    private bool Set(string key, string value)
    {
        // 同一设置服务的缓存变更、持久化与 clean 提交共享顺序；不允许较旧写入覆盖较新会话值。
        // 与 _lock 分开，避免写库阻塞时把正常缓存读取也锁住。
        lock (_writeGate)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var old) && old == value && !_dirtyKeys.Contains(key)) return true;
                _cache[key] = value;
                _dirtyKeys.Add(key);
            }
            try
            {
                _library.SetSetting(key, value);
                lock (_lock) _dirtyKeys.Remove(key);
                return true;
            }
            catch (Exception ex)
            {
                // 会话内继续生效，保留 dirty 供同值重试；普通 UI 属性 setter 不向外抛写库异常。
                Log.Error($"保存设置失败（可重试）: {key}", ex);
                return false;
            }
        }
    }
}
