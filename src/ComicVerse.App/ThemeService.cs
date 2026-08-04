using System.Windows;

namespace ComicVerse.App;

public static class ThemeService
{
    public static string Current { get; private set; } = "dark";

    public static void Apply(string theme)
    {
        if (Application.Current is null) return;
        theme = theme == "light" ? "light" : "dark";
        Current = theme;

        var dicts = Application.Current.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Theme", StringComparison.OrdinalIgnoreCase) == true);
        if (old is not null) dicts.Remove(old);

        var uri = new Uri($"pack://application:,,,/Resources/{theme}Theme.xaml", UriKind.RelativeOrAbsolute);
        dicts.Insert(0, new ResourceDictionary { Source = uri });
    }

    public static void Toggle() => Apply(Current == "dark" ? "light" : "dark");
}
