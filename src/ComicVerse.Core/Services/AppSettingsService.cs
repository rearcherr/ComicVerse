using System.Globalization;
using System.Text.Json;
using ComicVerse.Core.Models;

namespace ComicVerse.Core.Services;

/// <summary>
/// 应用设置：主题、缓存、阅读偏好等，存于 SQLite settings 表。
/// 构造时一次性把 settings 表读进内存字典：读取从此不再碰 SQLite（都是 UI 线程同步调用，
/// 原先每读一个设置就是一次数据库往返）。写入先更新内存再落库，落库失败只记日志，
/// 保证拖动"缓存上限"滑杆时不会因为逐帧 upsert（WAL + synchronous=FULL）卡住 UI 线程。
/// </summary>
public sealed class AppSettingsService
{
    private readonly LibraryService _library;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public AppSettingsService(LibraryService library)
    {
        _library = library;
        Reload();
    }

    /// <summary>重新从数据库读取全部设置（外部直接改 settings 表后调用）。</summary>
    public void Reload()
    {
        try
        {
            var all = _library.GetSettings();
            lock (_lock)
            {
                _cache.Clear();
                foreach (var kv in all) _cache[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，使用默认值", ex);
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

    private void Set(string key, string value)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var old) && old == value) return;   // 值没变就不写库
            _cache[key] = value;
        }
        try
        {
            _library.SetSetting(key, value);
        }
        catch (Exception ex)
        {
            // 落库失败仍是"本次会话内生效"，下次启动会回到旧值；不应该让设置界面崩溃
            Log.Error($"保存设置失败: {key}", ex);
        }
    }
}
