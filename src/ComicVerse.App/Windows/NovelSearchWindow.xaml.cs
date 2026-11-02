using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComicVerse.Core;
using ComicVerse.Core.Novel;

namespace ComicVerse.App.Windows;

public partial class NovelSearchWindow : Window
{
    private readonly NovelBook _book;
    private readonly Func<NovelSearchMatch, CancellationToken, Task<bool>> _navigate;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _navigationCts;
    private NovelSearchResult _result = new(Array.Empty<NovelSearchMatch>(), false);
    private int _searchVersion;
    private bool _closed;
    private bool _ignoreSelection;
    private bool _searching;
    private NovelSearchMatch? _pressedSelectedMatch;

    public NovelSearchWindow(NovelBook book, Func<NovelSearchMatch, CancellationToken, Task<bool>> navigate)
    {
        _book = book;
        _navigate = navigate;
        InitializeComponent();
        BookTitleText.Text = book.Title;
        BookTitleText.ToolTip = book.Title;
        Closed += (_, _) =>
        {
            _closed = true;
            Cancel(ref _searchCts);
            Cancel(ref _navigationCts);
        };
        Loaded += (_, _) => FocusQuery();
    }

    public void FocusQuery() { QueryBox.Focus(); QueryBox.SelectAll(); }
    internal int ResultCount => _result.Matches.Count;
    internal bool IsTruncated => _result.IsTruncated;
    internal string StatusValue => StatusText.Text;
    internal async Task SearchForTestAsync(string query) { QueryBox.Text = query; await SearchAsync(); }
    internal async Task<bool> NavigateResultForTestAsync(int index)
    {
        if (index < 0 || index >= _result.Matches.Count) return false;
        _ignoreSelection = true;
        ResultsList.SelectedIndex = index;
        _ignoreSelection = false;
        return await NavigateSelectedAsync();
    }
    internal Task<bool> ActivateSelectedForTestAsync() => NavigateSelectedAsync();

    private static void Cancel(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || ResultsList is null) return;
        _searchVersion++;
        _searching = false;
        Cancel(ref _searchCts);
        Cancel(ref _navigationCts);
        _result = new(Array.Empty<NovelSearchMatch>(), false);
        _ignoreSelection = true;
        ResultsList.ItemsSource = _result.Matches;
        _ignoreSelection = false;
        StatusText.Text = "输入关键词后按 Enter 查找";
        UpdateNavigationButtons();
    }

    private async Task SearchAsync()
    {
        if (_closed) return;
        Cancel(ref _searchCts);
        Cancel(ref _navigationCts);
        int version = ++_searchVersion;
        var cts = _searchCts = new CancellationTokenSource();
        _searching = true;
        UpdateNavigationButtons();
        StatusText.Text = "正在查找…";
        try
        {
            var result = await NovelTextSearch.SearchAsync(_book, QueryBox.Text, ct: cts.Token);
            if (_closed || cts.IsCancellationRequested || version != _searchVersion) return;
            _result = result;
            _ignoreSelection = true;
            ResultsList.ItemsSource = result.Matches;
            ResultsList.SelectedIndex = -1;
            _ignoreSelection = false;
            _searching = false;
            StatusText.Text = string.IsNullOrWhiteSpace(QueryBox.Text) ? "请输入查找关键词"
                : result.Matches.Count == 0 ? "未找到匹配正文，试试其他关键词"
                : result.IsTruncated ? $"仅显示前 {result.Matches.Count} 处，请使用更具体的关键词"
                : $"找到 {result.Matches.Count} 处，选择结果查看正文";
            UpdateNavigationButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("小说正文查找失败", ex);
            if (!_closed && version == _searchVersion) StatusText.Text = "查找失败，请重试";
        }
        finally
        {
            if (!_closed && version == _searchVersion)
            {
                _searching = false;
                UpdateNavigationButtons();
            }
        }
    }

    private async Task<bool> NavigateSelectedAsync()
    {
        if (_closed || _searching || ResultsList.SelectedItem is not NovelSearchMatch match) return false;
        Cancel(ref _navigationCts);
        var cts = _navigationCts = new CancellationTokenSource();
        int selected = ResultsList.SelectedIndex;
        UpdateNavigationButtons();
        try
        {
            bool found = await _navigate(match, cts.Token);
            if (_closed || cts.IsCancellationRequested) return false;
            StatusText.Text = found
                ? $"第 {selected + 1} / {_result.Matches.Count} 处" + (_result.IsTruncated ? "，还有更多结果" : "")
                : "无法定位，请关闭后重新打开正文查找";
            return found;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Error("小说查找结果定位失败", ex);
            if (!_closed && !cts.IsCancellationRequested) StatusText.Text = "无法定位该结果，请重新查找";
            return false;
        }
    }

    private void UpdateNavigationButtons()
    {
        ResultsList.IsEnabled = !_searching;
        PreviousButton.IsEnabled = !_searching && ResultsList.SelectedIndex > 0;
        NextButton.IsEnabled = !_searching && ResultsList.SelectedIndex + 1 < _result.Matches.Count;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ignoreSelection) await NavigateSelectedAsync();
    }
    private void Results_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        _pressedSelectedMatch = item?.DataContext is NovelSearchMatch match
            && ReferenceEquals(match, ResultsList.SelectedItem) ? match : null;
    }
    private async void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var match = _pressedSelectedMatch;
        _pressedSelectedMatch = null;
        if (match is null || _searching) return;
        var item = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (ReferenceEquals(item?.DataContext, match) && ReferenceEquals(ResultsList.SelectedItem, match))
            await NavigateSelectedAsync();
    }
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (!_searching && ResultsList.SelectedIndex > 0) ResultsList.SelectedIndex--;
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!_searching && ResultsList.SelectedIndex + 1 < _result.Matches.Count) ResultsList.SelectedIndex++;
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && QueryBox.IsKeyboardFocusWithin) { e.Handled = true; await SearchAsync(); }
        else if (e.Key == Key.Enter && ResultsList.IsKeyboardFocusWithin) { e.Handled = true; await NavigateSelectedAsync(); }
        else if (e.Key == Key.F3)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Previous_Click(this, e);
            else Next_Click(this, e);
            e.Handled = true;
        }
    }
}
