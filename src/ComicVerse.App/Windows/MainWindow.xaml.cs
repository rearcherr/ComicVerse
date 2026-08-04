using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;
using Microsoft.Win32;

namespace ComicVerse.App.Windows;

public partial class MainWindow : Window
{
    private const double CardWidth = 166;
    private const double CardGap = 16;

    private readonly List<Book> _books = new();
    private string _filter = "all";
    private string _sort = "recent";
    private bool _gridMode = true;
    private DispatcherTimer? _searchTimer;

    public MainWindow()
    {
        InitializeComponent();
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshBooks();
        };
    }

    public void Refresh() => RefreshBooks();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.Settings.LibraryView == "list") ApplyListMode();
        else ApplyGridMode();
        RefreshBooks();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && _gridMode) RebuildRows();
    }

    private void RefreshBooks()
    {
        if (!IsLoaded) return;
        _books.Clear();
        _books.AddRange(App.Library.GetBooks(SearchBox?.Text.Trim() ?? "", _filter, _sort));
        RebuildRows();
        ListView.ItemsSource = _books;
        StatusText.Text = $"共 {_books.Count} 本书 · 图片缓存 {App.SharedCache.EstimatedBytes / (1024 * 1024)} MB";
        bool empty = _books.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfList.Visibility = _gridMode && !empty ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = !_gridMode && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildRows()
    {
        if (ShelfList is null) return;
        double width = ShelfList.ActualWidth > 10 ? ShelfList.ActualWidth : 1200;
        int columns = Math.Max(1, (int)((width + CardGap) / (CardWidth + CardGap)));
        var rows = new List<BookRow>();
        for (int i = 0; i < _books.Count; i += columns)
            rows.Add(new BookRow(_books.Skip(i).Take(columns).ToList()));
        ShelfList.ItemsSource = rows;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _filter = FilterComic.IsChecked == true ? "comic" : FilterNovel.IsChecked == true ? "novel" : "all";
        RefreshBooks();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex < 0) return;
        _sort = SortBox.SelectedIndex switch
        {
            1 => "title",
            2 => "added",
            _ => "recent"
        };
        RefreshBooks();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择漫画或小说",
            Filter = "支持的格式|*.cbz;*.zip;*.cbr;*.rar;*.cbt;*.tar;*.cb7;*.7z;*.pdf;*.txt;*.epub|漫画|*.cbz;*.zip;*.cbr;*.rar;*.cbt;*.tar;*.cb7;*.7z;*.pdf|小说|*.txt;*.epub"
        };
        if (App.Settings.LastImportDir.Length > 0 && Directory.Exists(App.Settings.LastImportDir))
            dlg.InitialDirectory = App.Settings.LastImportDir;
        if (dlg.ShowDialog(this) == true)
        {
            App.Settings.LastImportDir = Path.GetDirectoryName(dlg.FileName) ?? "";
            _ = ImportAsync(dlg.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择漫画/小说文件夹" };
        if (App.Settings.LastImportDir.Length > 0 && Directory.Exists(App.Settings.LastImportDir))
            dlg.InitialDirectory = App.Settings.LastImportDir;
        if (dlg.ShowDialog(this) == true)
        {
            App.Settings.LastImportDir = dlg.FolderName;
            _ = ImportAsync(new[] { dlg.FolderName });
        }
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        StatusText.Text = "正在扫描文件…";
        var progress = new Progress<ImportProgress>(p =>
            StatusText.Text = $"正在导入 ({p.Done}/{p.Total})：{Path.GetFileName(p.Current.TrimEnd(Path.DirectorySeparatorChar))}");
        var result = await App.Importer.ImportAsync(paths, progress);
        RefreshBooks();
        if (result.Failed.Count == 0)
        {
            StatusText.Text = $"导入完成：新增 {result.Imported} 本，更新 {result.Updated} 本";
        }
        else
        {
            StatusText.Text = $"导入完成：新增 {result.Imported} 本，更新 {result.Updated} 本，失败 {result.Failed.Count} 个";
            MessageBox.Show(this, "以下文件无法导入：\n\n" + string.Join("\n", result.Failed.Take(12)),
                "导入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BookCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Book book)
            OpenReader(book);
    }

    private void BookCard_ContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.Tag is not Book book) return;
        var menu = new ContextMenu();

        var continueItem = new MenuItem { Header = "继续阅读" };
        continueItem.Click += (_, _) => OpenReader(book);
        var restartItem = new MenuItem { Header = "从头开始阅读" };
        restartItem.Click += (_, _) => OpenReader(book, fromStart: true);
        var resetItem = new MenuItem { Header = "重置阅读进度" };
        resetItem.Click += (_, _) =>
        {
            App.Library.SaveProgress(book.Id, 0, 0, 0, 0);
            RefreshBooks();
        };
        var openFolderItem = new MenuItem { Header = "打开所在文件夹" };
        openFolderItem.Click += (_, _) =>
        {
            string dir = Directory.Exists(book.FilePath) ? book.FilePath : Path.GetDirectoryName(book.FilePath) ?? "";
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        };
        var removeItem = new MenuItem { Header = "移出书架（不删除文件）" };
        removeItem.Click += (_, _) =>
        {
            App.Library.RemoveBook(book.Id);
            RefreshBooks();
        };

        menu.Items.Add(continueItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openFolderItem);
        menu.Items.Add(removeItem);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenReader(Book book, bool fromStart = false)
    {
        try
        {
            var reader = new ReaderWindow(book, fromStart) { Owner = this };
            reader.Closed += (_, _) => RefreshBooks();
            reader.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void OpenBookByPath(string path)
    {
        _ = Task.Run(async () => await App.Importer.ImportAsync(new[] { path }))
            .ContinueWith(_ =>
            {
                var book = App.Library.GetBookByPath(Path.GetFullPath(path));
                if (book is null)
                {
                    MessageBox.Show(this, "无法打开该文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                OpenReader(book);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
        App.Settings.Theme = ThemeService.Current;
        ThemeButton.Content = new TextBlock
        {
            Text = ThemeService.Current == "dark" ? "☾" : "☀",
            FontSize = 16,
            Foreground = System.Windows.Media.Brushes.White
        };
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private void GridMode_Click(object sender, RoutedEventArgs e) => ApplyGridMode();

    private void ListMode_Click(object sender, RoutedEventArgs e) => ApplyListMode();

    private void ApplyGridMode()
    {
        _gridMode = true;
        App.Settings.LibraryView = "grid";
        GridModeButton.Opacity = 1;
        ListModeButton.Opacity = 0.45;
        RefreshBooks();
    }

    private void ApplyListMode()
    {
        _gridMode = false;
        App.Settings.LibraryView = "list";
        GridModeButton.Opacity = 0.45;
        ListModeButton.Opacity = 1;
        RefreshBooks();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DragOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _ = ImportAsync(paths);
        e.Handled = true;
    }
}

public sealed class BookRow
{
    public BookRow(IReadOnlyList<Book> books) => Books = books;
    public IReadOnlyList<Book> Books { get; }
}
