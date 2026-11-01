using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;
using Microsoft.Win32;

namespace ComicVerse.App.Windows;

public partial class MainWindow : Window
{
    private const double CardWidth = 166;
    private const double CardGap = 16;

    // 用可观察集合：列表视图直接绑定它，导入新书后无需重启即可刷新
    private readonly ObservableCollection<Book> _books = new();
    private string _filter = "all";
    private string _sort = "recent";
    private int _readingState;
    private Book? _continueBook;
    private bool _gridMode = true;
    private DispatcherTimer? _searchTimer;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + App.AppVersion;
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
        Closed += (_, _) => _searchTimer.Stop();
    }

    public void Refresh() => RefreshBooks();

    // 自检钩子（仅 --smoke 使用）：走真实导入流程，验证书架是否立即刷新
    internal Task TestImportAsync(string[] paths) => ImportAsync(paths);
    internal bool ShelfContains(string keyword) =>
        _books.Any(b => b.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || b.FilePath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    internal int ShelfCount => _books.Count;
    internal IReadOnlyList<long> VisibleBookIds => _books.Select(b => b.Id).ToList();
    internal string EmptyHeadingText => EmptyHeading.Text;
    internal long? ContinueBookId => _continueBook?.Id;
    internal void TestSetShelfQuery(string search, int readingState, string type = "all")
    {
        SearchBox.Text = search;
        ReadingStateBox.SelectedIndex = readingState;
        FilterAll.IsChecked = type == "all";
        FilterComic.IsChecked = type == "comic";
        FilterNovel.IsChecked = type == "novel";
        _searchTimer?.Stop();
        RefreshBooks();
    }
    internal void TestResetProgress(long bookId) => ResetBookProgress(bookId);

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
        var allBooks = App.Library.GetBooks();
        var list = App.Library.GetBooks(SearchBox?.Text.Trim() ?? "", _filter, _sort)
            .Where(b => _readingState switch
            {
                1 => b.LastReadTime == default && b.Progress < 1,
                2 => b.LastReadTime != default && b.Progress < 1,
                3 => b.Progress >= 1,
                _ => true
            }).ToList();
        // 轻量校验源文件是否还在：失联的书在书架上标记出来，避免用户反复点开报错却无从判断
        try
        {
            App.Library.EnsureFilesPresent(list);
        }
        catch (Exception ex)
        {
            Log.Error("校验源文件是否存在失败", ex);
        }
        foreach (var b in list)
            _books.Add(b);
        RebuildRows();
        if (ListView.ItemsSource is null) ListView.ItemsSource = _books;
        StatusText.Text = $"显示 {_books.Count} / {allBooks.Count} 本书 · 图片缓存 {App.SharedCache.EstimatedBytes / (1024 * 1024)} MB";
        bool empty = _books.Count == 0;
        bool libraryEmpty = allBooks.Count == 0;
        bool searching = !string.IsNullOrWhiteSpace(SearchBox?.Text);
        EmptyHeading.Text = libraryEmpty ? "书架还是空的" : searching ? "未找到匹配的书籍" : "当前筛选下没有书籍";
        EmptyHint.Text = libraryEmpty ? "把漫画或小说文件/文件夹拖进窗口，或点击「添加文件」开始"
            : searching ? "试试其他书名或路径，或清除搜索与筛选" : "切换类型或阅读状态，查看其他书籍";
        ClearFiltersButton.Visibility = !libraryEmpty && empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfList.Visibility = _gridMode && !empty ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = !_gridMode && !empty ? Visibility.Visible : Visibility.Collapsed;
        // 继续阅读不受当前筛选影响，跳过读完的书与已失联的源文件。
        _continueBook = allBooks.Where(b => b.LastReadTime != default && b.Progress < 1)
            .OrderByDescending(b => b.LastReadTime)
            .FirstOrDefault(b => File.Exists(b.FilePath) || Directory.Exists(b.FilePath));
        if (_continueBook?.Error == LibraryService.MissingErrorText)
        {
            // 移动硬盘重新连接后，这本书可能不在当前筛选结果中，也应清除过时的失联标记。
            App.Library.ClearMissing(_continueBook.Id);
            _continueBook.Error = null;
        }
        ContinueButton.IsEnabled = _continueBook is not null;
        ContinueButton.Content = new TextBlock
        {
            Text = _continueBook is null ? "继续阅读" : "继续阅读：" + _continueBook.Title,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ContinueButton.ToolTip = _continueBook?.Title ?? "打开并阅读一本书后，可在这里继续";
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

    private void ReadingState_Changed(object sender, SelectionChangedEventArgs e)
    {
        _readingState = Math.Max(0, ReadingStateBox.SelectedIndex);
        RefreshBooks();
    }

    private void ContinueReading_Click(object sender, RoutedEventArgs e)
    {
        if (_continueBook is not null) OpenReader(_continueBook);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _searchTimer?.Stop();
        SearchBox.Clear();
        ReadingStateBox.SelectedIndex = 0;
        FilterAll.IsChecked = true;
        _searchTimer?.Stop();
        RefreshBooks();
        SearchBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            _searchTimer?.Stop();
            RefreshBooks();
            e.Handled = true;
        }
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
            ResetBookProgress(book.Id);
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
            // 先关掉这本书的阅读窗口，否则窗口持有的 _book.Id 会失效、并可能在随后把进度写回
            CloseReadersFor(book.Id);
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

    private void ResetBookProgress(long bookId)
    {
        // Closing 会保存当前位置，必须先关窗再清零；模式与缩放仍保留。
        CloseReadersFor(bookId);
        App.Library.ResetProgress(bookId);
        RefreshBooks();
    }

    /// <summary>关闭所有正在显示该书的阅读窗口（重置进度/移出书架前必须先做，避免残留窗口写回旧状态）。</summary>
    private void CloseReadersFor(long bookId)
    {
        foreach (var w in Application.Current.Windows.OfType<ReaderWindow>().ToList())
        {
            if (w.BookId != bookId) continue;
            try { w.Close(); } catch { /* 关不掉也不阻塞主流程 */ }
        }
    }

    private void OpenReader(Book book, bool fromStart = false)
    {
        try
        {
            var existing = Application.Current.Windows.OfType<ReaderWindow>()
                .FirstOrDefault(w => w.BookId == book.Id);
            if (existing is not null)
            {
                if (!fromStart)
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    existing.Focus();
                    return;
                }
                existing.Close();
            }

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
            Text = ThemeService.Current == "dark" ? "\uE708" : "\uE706",
            FontSize = 15,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
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
