using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ComicVerse.Core.Models;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    // The keyboard shortcut and automation both execute this production command.
    public static readonly RoutedCommand PageJumpCommand = new(nameof(PageJumpCommand), typeof(ReaderWindow));

    private bool IsComicPageJumpOpen => PageJumpPanel.Visibility == Visibility.Visible;

    private bool CanJumpToComicPage => !_closed && _interactionReady && _book.Type == BookType.Comic
        && _loader is { PageCount: > 0 } && _mode is "paged" or "double" or "webtoon"
        && (_mode != "webtoon" || (WebtoonView.IsReady && !_webtoonRestoringPosition));

    private void InstallComicPageJumpCommand()
    {
        CommandBindings.Add(new CommandBinding(PageJumpCommand,
            (_, e) => { OpenComicPageJump(); e.Handled = true; },
            (_, e) => { e.CanExecute = CanJumpToComicPage; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(PageJumpCommand, new KeyGesture(Key.G, ModifierKeys.Control)));
    }

    private void UpdateComicPageJumpAvailability()
    {
        bool comic = _book.Type == BookType.Comic;
        bool ready = CanJumpToComicPage;
        // Keep the existing TextBlock visible and undimmed for novels.
        PageJumpButton.IsHitTestVisible = comic;
        PageJumpButton.Focusable = PageJumpButton.IsTabStop = comic;
        PageJumpButton.ToolTip = comic ? "跳到指定页 (Ctrl+G)" : null;
        PageJumpButton.IsEnabled = PageJumpConfirmButton.IsEnabled = ready;
        if (!ready) CloseComicPageJump();
        CommandManager.InvalidateRequerySuggested();
    }

    private void PageJumpButton_Click(object sender, RoutedEventArgs e) => OpenComicPageJump();

    private void OpenComicPageJump()
    {
        if (!CanJumpToComicPage) return;
        if (!IsComicPageJumpOpen)
        {
            _hideBarsTimer?.Stop();
            if (!_barsVisible) ToggleBars();
            int count = _loader!.PageCount;
            // Request cursors may point at a pending/failed page; prefill from presentation facts.
            int current = Math.Clamp(_comicPresentation?.Page ?? _comicInitialPage, 0, count - 1);
            PageJumpInput.Text = (current + 1).ToString(CultureInfo.InvariantCulture);
            PageJumpRangeText.Text = $"页码（1–{count}）";
            PageJumpErrorText.Text = "";
            PageJumpErrorText.Visibility = Visibility.Collapsed;
            PageJumpConfirmButton.IsEnabled = true;
            PageJumpPanel.Visibility = Visibility.Visible;
        }
        PageJumpInput.Focus();
        PageJumpInput.SelectAll();
    }

    private void CloseComicPageJump(bool restoreFocus = true)
    {
        if (!IsComicPageJumpOpen) return;
        PageJumpPanel.Visibility = Visibility.Collapsed;
        PageJumpInput.Clear();
        PageJumpRangeText.Text = PageJumpErrorText.Text = "";
        PageJumpErrorText.Visibility = Visibility.Collapsed;
        if (restoreFocus && !_closed) ContentGrid.Focus();
    }

    private void PageJumpConfirmButton_Click(object sender, RoutedEventArgs e) => CommitComicPageJump();

    private void PageJumpCancelButton_Click(object sender, RoutedEventArgs e) => CloseComicPageJump();

    private void PageJumpPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PageJumpPanel)) return;
        CloseComicPageJump();
        e.Handled = true;
    }

    private void PageJump_PreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseComicPageJump();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (PageJumpCancelButton.IsKeyboardFocusWithin) CloseComicPageJump();
            else CommitComicPageJump();
            e.Handled = true;
        }
        // Leave Tab, text editing and the Ctrl+G InputBinding to WPF.
    }

    private void CommitComicPageJump()
    {
        if (!IsComicPageJumpOpen) return;
        if (!CanJumpToComicPage)
        {
            UpdateComicPageJumpAvailability();
            return;
        }
        int count = _loader!.PageCount;
        string text = PageJumpInput.Text.Trim();
        if (text.Length == 0 || text.Any(c => c is < '0' or > '9')
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number < 1 || number > count)
        {
            PageJumpErrorText.Text = $"请输入 1–{count} 的整数页码";
            PageJumpErrorText.Visibility = Visibility.Visible;
            PageJumpInput.Focus();
            return;
        }

        int target = number - 1;
        CloseComicPageJump();
        switch (_mode)
        {
            case "double":
                CancelDoubleNavigation();
                // RenderDoubleSpread records the original intent before canonicalizing the group.
                _spreadStart = target;
                RenderDoubleSpread();
                break;
            case "webtoon":
                NavigateWebtoonToPage(target);
                break;
            default:
                LoadPageAsync(target);
                break;
        }
    }
}
