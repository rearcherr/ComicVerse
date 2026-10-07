using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace ComicVerse.Droid.Ui;

/// <summary>程序化构建界面用的小工具（dp/sp 换算、圆角背景、常用控件）。</summary>
public static class UiKit
{
    public static float Density =>
        Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;

    public static int Dp(double value) => (int)Math.Round(value * Density);

    public static Color Argb(string hex) => Color.ParseColor(hex);

    public static GradientDrawable Round(Color fill, double radiusDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetColor(fill);
        drawable.SetCornerRadius(Dp(radiusDp));
        return drawable;
    }

    public static GradientDrawable RoundStroke(Color fill, double radiusDp, Color stroke, double strokeDp)
    {
        var drawable = Round(fill, radiusDp);
        drawable.SetStroke(Math.Max(1, Dp(strokeDp)), stroke);
        return drawable;
    }

    public static GradientDrawable HeaderGradient()
    {
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetOrientation(GradientDrawable.Orientation.TopBottom);
        drawable.SetColors(new[] { AppTheme.HeaderTop.ToArgb(), AppTheme.HeaderBottom.ToArgb() });
        return drawable;
    }

    public static TextView Text(Context context, string text, double sp, Color color, bool bold = false)
    {
        var view = new TextView(context);
        view.Text = text;
        view.SetTextSize(ComplexUnitType.Sp, (float)sp);
        view.SetTextColor(color);
        if (bold) view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        return view;
    }

    public static TextView Chip(Context context, string text, bool active, double sp = 12.5)
    {
        var view = Text(context, text, sp, active ? Color.White : AppTheme.TextSecondary, active);
        view.Gravity = GravityFlags.Center;
        view.SetPadding(Dp(12), Dp(7), Dp(12), Dp(7));
        view.Background = active
            ? Round(AppTheme.AccentSolid, 8)
            : RoundStroke(AppTheme.SurfaceAlt, 8, AppTheme.Border, 1);
        view.Clickable = true;
        return view;
    }

    public static TextView IconButton(Context context, string text, double sp = 15)
    {
        var view = Text(context, text, sp, AppTheme.TextPrimary);
        view.Gravity = GravityFlags.Center;
        view.SetPadding(Dp(9), Dp(7), Dp(9), Dp(7));
        view.Background = Round(AppTheme.SurfaceAlt, 8);
        view.Clickable = true;
        view.SetMinWidth(Dp(36));
        return view;
    }

    public static View Divider(Context context, double thicknessDp = 1)
    {
        var view = new View(context);
        view.SetBackgroundColor(AppTheme.Border);
        view.LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(thicknessDp));
        return view;
    }

    public static void Toast(Context context, string message)
    {
        Android.Widget.Toast.MakeText(context, message, ToastLength.Short)?.Show();
    }

    public static int ScreenWidth(Context context) =>
        context.Resources?.DisplayMetrics?.WidthPixels ?? 1080;

    public static int ScreenHeight(Context context) =>
        context.Resources?.DisplayMetrics?.HeightPixels ?? 1920;
}
