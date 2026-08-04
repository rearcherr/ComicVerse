using System.Globalization;
using System.Text.Json;
using ComicVerse.Core.Models;

namespace ComicVerse.Core.Services;

/// <summary>应用设置：主题、缓存、阅读偏好等，存于 SQLite settings 表。</summary>
public sealed class AppSettingsService
{
    private readonly LibraryService _library;

    public AppSettingsService(LibraryService library)
    {
        _library = library;
    }

    public string Theme
    {
        get => Get("theme", "dark");
        set => _library.SetSetting("theme", value);
    }

    public long CacheLimitMb
    {
        get => long.TryParse(Get("cache_limit_mb", "400"), out var v) ? Math.Clamp(v, 128, 2048) : 400;
        set => _library.SetSetting("cache_limit_mb", value.ToString(CultureInfo.InvariantCulture));
    }

    public int PrefetchPages
    {
        get => int.TryParse(Get("prefetch_pages", "4"), out var v) ? Math.Clamp(v, 0, 8) : 4;
        set => _library.SetSetting("prefetch_pages", value.ToString(CultureInfo.InvariantCulture));
    }

    public bool LowPerformanceMode
    {
        get => Get("low_perf", "0") == "1";
        set => _library.SetSetting("low_perf", value ? "1" : "0");
    }

    public bool MangaRightToLeft
    {
        get => Get("manga_rtl", "0") == "1";
        set => _library.SetSetting("manga_rtl", value ? "1" : "0");
    }

    public bool AutoHideBars
    {
        get => Get("auto_hide_bars", "0") == "1";
        set => _library.SetSetting("auto_hide_bars", value ? "1" : "0");
    }

    public string LibraryView
    {
        get => Get("library_view", "grid");
        set => _library.SetSetting("library_view", value);
    }

    public NovelViewSettings NovelSettings
    {
        get
        {
            string? json = _library.GetSetting("novel_settings");
            if (json is null) return new NovelViewSettings();
            try
            {
                return JsonSerializer.Deserialize<NovelViewSettings>(json) ?? new NovelViewSettings();
            }
            catch
            {
                return new NovelViewSettings();
            }
        }
        set => _library.SetSetting("novel_settings", JsonSerializer.Serialize(value));
    }

    public string LastImportDir
    {
        get => Get("last_import_dir", "");
        set => _library.SetSetting("last_import_dir", value);
    }

    private string Get(string key, string fallback) => _library.GetSetting(key) ?? fallback;
}
