using Microsoft.Win32;

namespace ComicVerse.App.Helpers;

public static class FileAssociations
{
    public static void Register(string exePath)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ComicVerse.Document\shell\open\command"))
        {
            key.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        foreach (string ext in new[] { ".cbz", ".cbr", ".cbt", ".cb7", ".zip", ".rar", ".tar", ".7z", ".pdf", ".txt", ".epub" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext);
            key.SetValue("", "ComicVerse.Document");
        }
    }
}
