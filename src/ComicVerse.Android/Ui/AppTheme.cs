using Android.Graphics;

namespace ComicVerse.Droid.Ui;

/// <summary>与桌面端 DarkTheme/LightTheme 对齐的配色（中性灰阶 + 单一品牌玫红）。</summary>
public static class AppTheme
{
    public static bool Dark { get; set; } = true;

    private static Color P(string hex) => Color.ParseColor(hex);

    public static Color WindowBg => P(Dark ? "#0F1115" : "#F5F6F8");
    public static Color Surface => P(Dark ? "#16191F" : "#FFFFFF");
    public static Color SurfaceAlt => P(Dark ? "#1E222A" : "#EFF1F4");
    public static Color SurfaceHover => P(Dark ? "#272C35" : "#E3E6EB");
    public static Color TextPrimary => P(Dark ? "#F2F4F7" : "#14161A");
    public static Color TextSecondary => P(Dark ? "#A6ADB9" : "#545B67");
    public static Color TextFaint => P(Dark ? "#767D8A" : "#868D99");
    public static Color Border => P(Dark ? "#2B3038" : "#DCE0E6");
    public static Color Accent => P(Dark ? "#E4557F" : "#C0275F");
    public static Color AccentSolid => P(Dark ? "#C0275F" : "#AE1F55");
    public static Color AccentSoft => P(Dark ? "#33202A" : "#FBEDF2");
    public static Color ReaderBg => P(Dark ? "#0B0C10" : "#E9EBEF");
    public static Color ProgressTrack => P(Dark ? "#2B3038" : "#DCE0E6");
    public static Color Danger => P(Dark ? "#E06057" : "#B23A32");
    public static Color Success => P(Dark ? "#52BE8E" : "#2E7D5B");
    public static Color HeaderTop => P(Dark ? "#1A1E25" : "#FFFFFF");
    public static Color HeaderBottom => P(Dark ? "#14171D" : "#F1F3F6");
}
