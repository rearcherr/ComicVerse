using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.App.Windows;
using ComicVerse.Core.Models;

namespace ComicVerse.App;

public static partial class SmokeTest
{
    // Independent acceptance fixture. Actions use the public UI event/command routes;
    // existing test helpers only prepare data, observe pixels, and flush saved facts.
    private static async Task<(bool Ok, string Detail)> CheckComicPageJumpAsync(string outDir)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        bool originalRtl = App.Settings.MangaRightToLeft;
        bool originalAutoHide = App.Settings.AutoHideBars;
        string originalTheme = ThemeService.Current;
        try
        {
            App.Settings.MangaRightToLeft = false;
            App.Settings.AutoHideBars = false;
            ThemeService.Apply("dark");
            await PageJumpInputsAsync(outDir, checks);
            await PageJumpDoubleAsync(outDir, checks);
            await PageJumpWebtoonAsync(outDir, checks);
            await PageJumpFailuresAsync(outDir, checks);
            await PageJumpSingleAsync(outDir, checks);
            await PageJumpPendingAsync(outDir, checks);
            await PageJumpLongAndPdfAsync(outDir, checks);
            await PageJumpNovelAsync(outDir, checks);
        }
        finally
        {
            App.Settings.MangaRightToLeft = originalRtl;
            App.Settings.AutoHideBars = originalAutoHide;
            ThemeService.Apply(originalTheme);
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "comic-page-jump-details.json"),
            JsonSerializer.Serialize(checks.Select(c => new { c.Name, c.Ok, c.Detail }),
                new JsonSerializerOptions { WriteIndented = true }));
        string detail = $"漫画精确页码 {checks.Count(c => c.Ok)}/{checks.Count} — "
            + string.Join(" | ", checks.Select(c => $"{c.Name}={c.Ok}({c.Detail})"));
        return (checks.Count > 0 && checks.All(c => c.Ok), detail);
    }

    private static T PageJumpControl<T>(ReaderWindow reader, string name) where T : FrameworkElement
        => reader.FindName(name) as T ?? throw new InvalidOperationException($"Missing UI control {name} ({typeof(T).Name})");

    private static ReaderWindow PageJumpReader(Book book)
        => new(book) { Width = 980, Height = 720, ShowInTaskbar = false };

    private static bool PageJumpOpen(ReaderWindow reader)
        => PageJumpControl<FrameworkElement>(reader, "PageJumpPanel").IsVisible;

    private static void PageJumpClick(ReaderWindow reader, string name)
    {
        var button = PageJumpControl<Button>(reader, name);
        if (!button.IsEnabled || !button.IsVisible)
            throw new InvalidOperationException($"UI action unavailable: {name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static bool PageJumpKey(ReaderWindow reader, UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(reader)
            ?? throw new InvalidOperationException("Reader has no presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(args);
        }
        return args.Handled;
    }

    private static bool PageJumpCtrlG(ReaderWindow reader)
    {
        // Exercise the actual Ctrl+G InputBinding and routed command, without OS
        // input injection or a production-only test shortcut. Missing binding fails.
        var binding = reader.InputBindings.OfType<KeyBinding>()
            .SingleOrDefault(b => b.Key == Key.G && b.Modifiers == ModifierKeys.Control);
        if (binding?.Command is null) return false;
        if (binding.Command is RoutedCommand routed)
        {
            var target = binding.CommandTarget ?? reader;
            if (!routed.CanExecute(binding.CommandParameter, target)) return false;
            routed.Execute(binding.CommandParameter, target);
        }
        else
        {
            if (!binding.Command.CanExecute(binding.CommandParameter)) return false;
            binding.Command.Execute(binding.CommandParameter);
        }
        return true;
    }

    private static async Task<TextBox> PageJumpShowAsync(ReaderWindow reader)
    {
        if (!PageJumpOpen(reader)) PageJumpClick(reader, "PageJumpButton");
        var input = PageJumpControl<TextBox>(reader, "PageJumpInput");
        if (!await WaitLogicAuditAsync(() => PageJumpOpen(reader) && input.IsKeyboardFocused, 3))
            throw new InvalidOperationException("Page jump did not open with real keyboard focus");
        return input;
    }

    private static async Task PageJumpSubmitAsync(ReaderWindow reader, string text, bool enter = false)
    {
        var input = await PageJumpShowAsync(reader);
        input.Text = text; // Raises real TextChanged; models typing/pasted text without clipboard access.
        if (enter) PageJumpKey(reader, input, Key.Enter);
        else PageJumpClick(reader, "PageJumpConfirmButton");
    }

    private static string PageJumpDetail(ReaderWindow reader, Book book)
        => $"footer={reader.PageInfoText.Text};page={reader.CurrentPageNumber};spread={reader.CurrentSpreadStart};"
            + $"left={reader.DoubleLeftPageIndex};right={reader.DoubleRightPageIndex};"
            + $"offset={reader.WebtoonScrollOffset:F2};panel={PageJumpOpen(reader)};"
            + $"focus={(Keyboard.FocusedElement as FrameworkElement)?.Name};{OpcDb(book)}";

    private static bool PageJumpPaged(ReaderWindow reader, int index)
        => reader.IsComicPageLoaded && OpcPixels(reader.PageImage, index)
            && reader.PageInfoText.Text == $"{index + 1} / {reader.ComicPageCount}";

    private static bool PageJumpSpread(ReaderWindow reader, int first, int last, bool rtl = false)
    {
        int left = rtl && last >= 0 ? last : first;
        int right = rtl && last >= 0 ? first : last;
        return reader.IsDoubleReady && reader.DoubleLeftPageIndex == left && reader.DoubleRightPageIndex == right
            && OpcPixels(reader.LeftImage, left) && (right < 0 ? reader.RightImage.Source is null : OpcPixels(reader.RightImage, right))
            && reader.PageInfoText.Text == (last < 0 ? $"{first + 1} / {reader.ComicPageCount}"
                : $"{first + 1}-{last + 1} / {reader.ComicPageCount}");
    }

    private static bool PageJumpInside(FrameworkElement element, FrameworkElement root)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var position = element.TranslatePoint(new Point(), root);
        return position.X >= -1 && position.Y >= -1
            && position.X + element.ActualWidth <= root.ActualWidth + 1
            && position.Y + element.ActualHeight <= root.ActualHeight + 1;
    }

    private static async Task PageJumpInputsAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码输入与键盘", 9));
            App.Library.SaveProgress(book.Id, 1.0 / 9, 0, 0, 0, "paged", 1);
            reader = PageJumpReader(book);
            checks.Add(("AC07 初始化前不可提交页码", !PageJumpControl<Button>(reader, "PageJumpButton").IsEnabled
                && !PageJumpCtrlG(reader) && !PageJumpOpen(reader), "Before Show: actual command CanExecute=false"));
            reader.Show();
            bool ready = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 0));
            var input = await PageJumpShowAsync(reader);
            checks.Add(("AC01 页号按钮打开并预填全选", ready && input.Text == "1"
                && input.SelectionLength == input.Text.Length
                && PageJumpControl<TextBlock>(reader, "PageJumpRangeText").Text.Contains('9'), PageJumpDetail(reader, book)));
            Capture(reader, Path.Combine(outDir, "page-jump-open-980.png"));
            await PageJumpSubmitAsync(reader, "4");
            bool jumped = await WaitLogicAuditAsync(() => !PageJumpOpen(reader) && PageJumpPaged(reader, 3));
            reader.TestFlushSave();
            checks.Add(("AC01 按钮提交页4像素底栏DB", jumped && OpcSaved(book, 3, "paged", 4.0 / 9)
                && reader.ContentGrid.IsKeyboardFocused, PageJumpDetail(reader, book)));

            string[] invalid = ["", "   ", "0", "-1", "+1", "1.5", "abc", "1a", "10", "2147483648", "999999999999999999999999999999", "４"];
            for (int i = 0; i < invalid.Length; i++)
            {
                input = await PageJumpShowAsync(reader);
                await PageJumpSubmitAsync(reader, invalid[i], enter: i % 2 == 0);
                bool rejected = await WaitLogicAuditAsync(() => PageJumpOpen(reader)
                    && PageJumpControl<TextBlock>(reader, "PageJumpErrorText").IsVisible
                    && !string.IsNullOrWhiteSpace(PageJumpControl<TextBlock>(reader, "PageJumpErrorText").Text), 2);
                reader.TestFlushSave();
                checks.Add(($"AC05 拒绝非法输入[{i}]", rejected && input.Text == invalid[i] && input.IsKeyboardFocused
                    && PageJumpPaged(reader, 3) && OpcSaved(book, 3, "paged", 4.0 / 9),
                    $"input={JsonSerializer.Serialize(invalid[i])};error={PageJumpControl<TextBlock>(reader, "PageJumpErrorText").Text};{PageJumpDetail(reader, book)}"));
                if (i == 6) Capture(reader, Path.Combine(outDir, "page-jump-invalid-980.png"));
            }
            await PageJumpSubmitAsync(reader, " 001 ", enter: true);
            bool corrected = await WaitLogicAuditAsync(() => !PageJumpOpen(reader) && PageJumpPaged(reader, 0));
            checks.Add(("AC04/05 错误后Enter接受空白与前导零", corrected && reader.ContentGrid.IsKeyboardFocused, PageJumpDetail(reader, book)));
            await PageJumpSubmitAsync(reader, "9", enter: true);
            bool last = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 8) && !PageJumpOpen(reader));
            Capture(reader, Path.Combine(outDir, "page-jump-paged-last.png"));
            await PageJumpSubmitAsync(reader, "9");
            bool same = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 8) && !PageJumpOpen(reader));
            checks.Add(("AC04 末页与重复当前页", last && same, PageJumpDetail(reader, book)));

            input = await PageJumpShowAsync(reader);
            input.Text = "2";
            bool escape = PageJumpKey(reader, input, Key.Escape);
            bool cancelled = await WaitLogicAuditAsync(() => !PageJumpOpen(reader) && reader.ContentGrid.IsKeyboardFocused, 2);
            checks.Add(("AC06 Esc取消不关闭阅读器", escape && cancelled && reader.IsVisible && PageJumpPaged(reader, 8), PageJumpDetail(reader, book)));
            input = await PageJumpShowAsync(reader);
            input.Text = "2";
            PageJumpClick(reader, "PageJumpCancelButton");
            checks.Add(("AC06 取消按钮不提交", !PageJumpOpen(reader) && PageJumpPaged(reader, 8), PageJumpDetail(reader, book)));

            input = await PageJumpShowAsync(reader);
            input.Text = "2";
            var panel = PageJumpControl<FrameworkElement>(reader, "PageJumpPanel");
            panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            checks.Add(("AC06 点击浮层背景取消草稿", !PageJumpOpen(reader) && reader.IsVisible
                && PageJumpPaged(reader, 8) && reader.ContentGrid.IsKeyboardFocused, PageJumpDetail(reader, book)));
            input = await PageJumpShowAsync(reader);
            var confirm = PageJumpControl<Button>(reader, "PageJumpConfirmButton");
            var cancel = PageJumpControl<Button>(reader, "PageJumpCancelButton");
            bool cycle = KeyboardNavigation.GetTabNavigation(panel) == KeyboardNavigationMode.Cycle;
            foreach (var expected in new FrameworkElement[] { confirm, cancel, input })
            {
                bool moved = (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) == true;
                cycle &= moved && await WaitLogicAuditAsync(() => expected.IsKeyboardFocused, 1);
            }
            bool reverse = input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            checks.Add(("AC06 WPF焦点遍历正反向在浮层循环", cycle && reverse && cancel.IsKeyboardFocused
                && PageJumpOpen(reader) && PageJumpPaged(reader, 8), "MoveFocus Next: input/confirm/cancel/input; Previous: input/cancel; " + PageJumpDetail(reader, book)));
            PageJumpClick(reader, "PageJumpCancelButton");

            bool command = PageJumpCtrlG(reader);
            bool focused = await WaitLogicAuditAsync(() => PageJumpOpen(reader) && input.IsKeyboardFocused, 2);
            input.Text = "5";
            bool repeated = PageJumpCtrlG(reader);
            bool selected = await WaitLogicAuditAsync(() => input.IsKeyboardFocused && input.SelectionLength == input.Text.Length, 2);
            checks.Add(("AC01/06 CtrlG真实绑定与重复全选", command && focused && repeated && selected && input.Text == "5", PageJumpDetail(reader, book)));
            if (!PageJumpOpen(reader)) input = await PageJumpShowAsync(reader);
            var oldStyle = reader.WindowStyle;
            var oldState = reader.WindowState;
            var oldTopBar = reader.TopBar.Visibility;
            int bookmarks = App.Library.GetBookmarks(book.Id).Count;
            bool guarded = true;
            foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.Space, Key.Delete, Key.Back, Key.PageUp, Key.PageDown, Key.B, Key.F, Key.M })
            {
                PageJumpKey(reader, input, key);
                await reader.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                guarded &= reader.IsVisible && PageJumpOpen(reader) && PageJumpPaged(reader, 8)
                    && reader.WindowStyle == oldStyle && reader.WindowState == oldState && reader.TopBar.Visibility == oldTopBar
                    && App.Library.GetBookmarks(book.Id).Count == bookmarks;
            }
            checks.Add(("AC06 编辑按键不翻页书签全屏或隐藏栏", guarded, PageJumpDetail(reader, book)));
            PageJumpKey(reader, input, Key.Escape);

            reader.ContentGrid.Focus();
            PageJumpKey(reader, reader.ContentGrid, Key.M);
            bool hidden = reader.TopBar.Visibility == Visibility.Collapsed;
            bool hiddenCommand = PageJumpCtrlG(reader);
            bool hiddenOpen = await WaitLogicAuditAsync(() => PageJumpOpen(reader) && input.IsKeyboardFocused, 2);
            checks.Add(("AC06 工具栏隐藏时CtrlG可输入", hidden && hiddenCommand && hiddenOpen, PageJumpDetail(reader, book)));
            if (PageJumpOpen(reader)) PageJumpKey(reader, input, Key.Escape);
            if (reader.TopBar.Visibility != Visibility.Visible) PageJumpKey(reader, reader.ContentGrid, Key.M);
            reader.ContentGrid.Focus();
            PageJumpKey(reader, reader.ContentGrid, Key.F);
            bool fullscreen = reader.WindowState == WindowState.Maximized && reader.WindowStyle == WindowStyle.None;
            bool fullCommand = PageJumpCtrlG(reader);
            bool fullOpen = await WaitLogicAuditAsync(() => PageJumpOpen(reader) && input.IsKeyboardFocused, 2);
            Capture(reader, Path.Combine(outDir, "page-jump-fullscreen.png"));
            if (PageJumpOpen(reader)) PageJumpKey(reader, input, Key.Escape);
            checks.Add(("AC06 全屏打开Esc只取消浮层", fullscreen && fullCommand && fullOpen && reader.IsVisible
                && !PageJumpOpen(reader) && reader.WindowState == WindowState.Maximized && PageJumpPaged(reader, 8), PageJumpDetail(reader, book)));
            PageJumpKey(reader, reader.ContentGrid, Key.F);

            reader.Width = reader.MinWidth;
            reader.Height = reader.MinHeight;
            input = await PageJumpShowAsync(reader);
            await PageJumpSubmitAsync(reader, "0");
            reader.UpdateLayout();
            string[] names = ["PageJumpInput", "PageJumpRangeText", "PageJumpErrorText", "PageJumpConfirmButton", "PageJumpCancelButton"];
            checks.Add(("AC11 最小窗口错误态控件在可见边界内", names.All(n => PageJumpInside(PageJumpControl<FrameworkElement>(reader, n), reader)),
                $"size={reader.ActualWidth}x{reader.ActualHeight};min={reader.MinWidth}x{reader.MinHeight}"));
            Capture(reader, Path.Combine(outDir, "page-jump-minimum-error.png"));
            ThemeService.Apply("light");
            reader.UpdateLayout();
            checks.Add(("AC11 浅色最小窗口输入错误态", ThemeService.Current == "light"
                && names.All(n => PageJumpInside(PageJumpControl<FrameworkElement>(reader, n), reader)), PageJumpDetail(reader, book)));
            Capture(reader, Path.Combine(outDir, "page-jump-minimum-light-error.png"));
            ThemeService.Apply("dark");
            PageJumpClick(reader, "PageJumpCancelButton");
            reader.Close();
            checks.Add(("AC10 关闭保存末页", OpcSaved(book, 8, "paged", 1), OpcDb(book)));
            reader = PageJumpReader(book);
            reader.Show();
            bool reopened = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 8));
            checks.Add(("AC10 重开成功页且不恢复输入草稿", reopened && !PageJumpOpen(reader), PageJumpDetail(reader, book)));
        }
        catch (Exception ex) { checks.Add(("页码输入键盘组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task PageJumpDoubleAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码双页RTL", 9));
            App.Library.SaveProgress(book.Id, 2.0 / 9, 0, 0, 0, "double", 1);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => PageJumpSpread(reader, 0, 1))) throw new InvalidOperationException("Double baseline not ready");
            foreach (int page in new[] { 3, 4, 9, 1 })
            {
                int first = (page - 1) / 2 * 2;
                int last = first + 1 < 9 ? first + 1 : -1;
                await PageJumpSubmitAsync(reader, page.ToString(CultureInfo.InvariantCulture), enter: page % 2 == 0);
                bool loaded = await WaitLogicAuditAsync(() => !PageJumpOpen(reader) && PageJumpSpread(reader, first, last));
                reader.TestFlushSave();
                checks.Add(($"AC02 双页输入{page}规范组", loaded && OpcSaved(book, first, "double", (last < 0 ? first + 1.0 : last + 1.0) / 9), PageJumpDetail(reader, book)));
            }
            PageJumpClick(reader, "RtlButton");
            if (!await WaitLogicAuditAsync(() => PageJumpSpread(reader, 0, 1, true))) throw new InvalidOperationException("RTL baseline not ready");
            await PageJumpSubmitAsync(reader, "4", enter: true);
            bool rtl = await WaitLogicAuditAsync(() => PageJumpSpread(reader, 2, 3, true));
            reader.TestFlushSave();
            checks.Add(("AC02 RTL仅交换左右且保存组首", rtl && OpcSaved(book, 2, "double", 4.0 / 9), PageJumpDetail(reader, book)));
            Capture(reader, Path.Combine(outDir, "page-jump-double-rtl-980.png"));
            reader.Close();
            reader = PageJumpReader(book);
            reader.Show();
            bool restored = await WaitLogicAuditAsync(() => PageJumpSpread(reader, 2, 3, true));
            checks.Add(("AC10 双页关闭重开完整组", restored, PageJumpDetail(reader, book)));
            reader.Close();
            App.Settings.MangaRightToLeft = false;

            var mixed = await ImportLogicAuditComicAsync(outDir, "QA-页码横竖混排", (240, 600), (900, 500), (240, 600), (240, 600), (240, 600));
            App.Library.SaveProgress(mixed.Id, 0.2, 0, 0, 0, "double", 1);
            reader = PageJumpReader(mixed);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsDoubleReady)) throw new InvalidOperationException("Mixed baseline not ready");
            await PageJumpSubmitAsync(reader, "2");
            bool landscape = await WaitLogicAuditAsync(() => reader.IsDoubleReady && reader.DoubleLeftPageIndex == 1
                && reader.DoubleRightPageIndex == -1 && LogicAuditPagePixelsMatch(reader.LeftImage, 1));
            checks.Add(("AC02 横页单独显示", landscape && reader.PageInfoText.Text == "2 / 5", PageJumpDetail(reader, mixed)));
            await PageJumpSubmitAsync(reader, "4");
            bool pair = await WaitLogicAuditAsync(() => reader.IsDoubleReady && reader.DoubleLeftPageIndex == 2 && reader.DoubleRightPageIndex == 3
                && LogicAuditPagePixelsMatch(reader.LeftImage, 2) && LogicAuditPagePixelsMatch(reader.RightImage, 3));
            reader.TestFlushSave();
            checks.Add(("AC02 横页后重新计算规范双页组", pair && reader.PageInfoText.Text == "3-4 / 5" && OpcSaved(mixed, 2, "double", 0.8), PageJumpDetail(reader, mixed)));
            Capture(reader, Path.Combine(outDir, "page-jump-double-mixed-980.png"));
        }
        catch (Exception ex) { checks.Add(("页码双页组异常", false, ex.ToString())); }
        finally { reader?.Close(); App.Settings.MangaRightToLeft = false; }
    }

    private static async Task PageJumpWebtoonAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码条漫实际offset", 9));
            App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "webtoon", 0.2);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonBlankStripCount == 0)) throw new InvalidOperationException("Webtoon baseline not ready");
            var scroll = reader.WebtoonView.FindName("Scroll") as ScrollViewer ?? throw new InvalidOperationException("Missing webtoon ScrollViewer");
            foreach (int page in new[] { 5, 6, 1, 9 })
            {
                await PageJumpSubmitAsync(reader, page.ToString(CultureInfo.InvariantCulture), enter: page % 2 == 1);
                double top = (page - 1) * 600 * reader.WebtoonView.CanvasWidth / 240;
                double expected = Math.Min(top, scroll.ScrollableHeight);
                bool arrived = await WaitLogicAuditAsync(() => !PageJumpOpen(reader) && reader.IsWebtoonReady
                    && Math.Abs(scroll.VerticalOffset - expected) <= 1 && reader.WebtoonRenderedLoadedCount > 0 && reader.WebtoonBlankStripCount == 0);
                reader.TestFlushSave();
                var saved = App.Library.GetProgress(book.Id);
                bool inViewport = top < scroll.VerticalOffset + scroll.ViewportHeight + 1
                    && top + 600 * reader.WebtoonView.CanvasWidth / 240 > scroll.VerticalOffset;
                checks.Add(($"AC03 条漫输入{page}真实视口与DB", arrived && inViewport && saved is { Mode: "webtoon" }
                    && saved.PageIndex == reader.WebtoonCurrentPage && Math.Abs(saved.ScrollOffset - reader.WebtoonView.ScrollFraction) < 0.002
                    && reader.PageInfoText.Text == $"{reader.WebtoonCurrentPage + 1} / 9",
                    $"targetTop={top:F2};expected={expected:F2};actual={scroll.VerticalOffset:F2};viewport={scroll.ViewportHeight:F2};{PageJumpDetail(reader, book)}"));
                if (page == 9)
                {
                    checks.Add(("AC03 末页实际触发底部clamp", top > scroll.ScrollableHeight + 1 && arrived, $"top={top:F2};max={scroll.ScrollableHeight:F2}"));
                    Capture(reader, Path.Combine(outDir, "page-jump-webtoon-last-clamp-980.png"));
                }
            }
            double previousOffset = scroll.VerticalOffset;
            int previousPage = reader.WebtoonCurrentPage;
            reader.Close();
            reader = PageJumpReader(book);
            reader.Show();
            bool restored = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == previousPage
                && Math.Abs(reader.WebtoonScrollOffset - previousOffset) <= 1 && reader.WebtoonBlankStripCount == 0);
            checks.Add(("AC10 条漫重开恢复实际offset", restored, $"previousOffset={previousOffset:F2};{PageJumpDetail(reader, book)}"));
            reader.Close();
            var unequal = await ImportLogicAuditComicAsync(outDir, "QA-页码不等高条漫", (240, 600), (240, 180), (240, 900), (240, 300), (240, 500));
            App.Library.SaveProgress(unequal.Id, 0, 0, 0, 0, "webtoon", 0.5);
            reader = PageJumpReader(unequal);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonBlankStripCount == 0)) throw new InvalidOperationException("Unequal webtoon baseline not ready");
            await PageJumpSubmitAsync(reader, "3", enter: true);
            double unequalTop = (600 + 180) * reader.WebtoonView.CanvasWidth / 240;
            bool precise = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 2
                && Math.Abs(reader.WebtoonScrollOffset - unequalTop) <= 1 && reader.WebtoonBlankStripCount == 0);
            reader.TestFlushSave();
            checks.Add(("AC03 不等高条漫按目标页顶部而非比例", precise && App.Library.GetProgress(unequal.Id) is { PageIndex: 2, Mode: "webtoon" },
                $"expectedTop={unequalTop:F2};{PageJumpDetail(reader, unequal)}"));
            Capture(reader, Path.Combine(outDir, "page-jump-webtoon-unequal-980.png"));
        }
        catch (Exception ex) { checks.Add(("页码条漫组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task PageJumpFailuresAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        foreach (string mode in new[] { "paged", "double" })
        {
            ReaderWindow? reader = null;
            try
            {
                var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码坏目标-" + mode, 6, 3));
                int first = mode == "paged" ? 1 : 0;
                App.Library.SaveProgress(book.Id, 2.0 / 6, first, 0, 0, mode, 1);
                reader = PageJumpReader(book);
                reader.Show();
                bool Baseline() => mode == "paged" ? PageJumpPaged(reader, 1) : PageJumpSpread(reader, 0, 1);
                if (!await WaitLogicAuditAsync(Baseline)) throw new InvalidOperationException("Failure baseline not ready");
                await PageJumpSubmitAsync(reader, "4", enter: true);
                bool failed = await WaitLogicAuditAsync(() => mode == "paged"
                    ? reader.LoadingPanel.IsVisible && reader.LoadingText.Text.Contains("失败")
                    : reader.DoubleLoading.IsVisible && reader.DoubleLoadingText.Text.Contains("失败"));
                reader.TestFlushSave();
                bool retainedPixels = mode == "paged" ? OpcPixels(reader.PageImage, 1)
                    : OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1);
                string footer = mode == "paged" ? "2 / 6" : "1-2 / 6";
                checks.Add(($"AC08 {mode}坏目标保留成功画面和DB", failed && retainedPixels && reader.PageInfoText.Text == footer
                    && OpcSaved(book, first, mode, 2.0 / 6), PageJumpDetail(reader, book)));
                Capture(reader, Path.Combine(outDir, $"page-jump-{mode}-failure-980.png"));
                reader.Close();
                checks.Add(($"AC10 {mode}失败关闭不保存目标", OpcSaved(book, first, mode, 2.0 / 6), OpcDb(book)));
                reader = PageJumpReader(book);
                reader.Show();
                bool reopened = await WaitLogicAuditAsync(Baseline);
                checks.Add(($"AC10 {mode}失败重开回成功位置", reopened && !PageJumpOpen(reader), PageJumpDetail(reader, book)));
                await PageJumpSubmitAsync(reader, "6");
                bool recovered = await WaitLogicAuditAsync(() => mode == "paged" ? PageJumpPaged(reader, 5) : PageJumpSpread(reader, 4, 5));
                reader.TestFlushSave();
                checks.Add(($"AC08 {mode}再次输入可读目标恢复", recovered && OpcSaved(book, mode == "paged" ? 5 : 4, mode, 1), PageJumpDetail(reader, book)));
            }
            catch (Exception ex) { checks.Add(($"页码坏目标{mode}组异常", false, ex.ToString())); }
            finally { reader?.Close(); }
        }
    }

    private static async Task PageJumpSingleAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码单页书", 1));
            App.Library.SaveProgress(book.Id, 1, 0, 0, 0, "paged", 1);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => PageJumpPaged(reader, 0))) throw new InvalidOperationException("Single-page baseline not ready");
            await PageJumpSubmitAsync(reader, "1", enter: true);
            bool paged = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 0) && !PageJumpOpen(reader));
            reader.ModeDouble.IsChecked = true;
            if (!await WaitLogicAuditAsync(() => PageJumpSpread(reader, 0, -1))) throw new InvalidOperationException("Single spread not ready");
            await PageJumpSubmitAsync(reader, "1");
            bool spread = await WaitLogicAuditAsync(() => PageJumpSpread(reader, 0, -1) && !PageJumpOpen(reader));
            checks.Add(("AC04 单页书分页双页均合法", paged && spread, PageJumpDetail(reader, book)));
            reader.Close();
            App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "webtoon", 0.2);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonBlankStripCount == 0)) throw new InvalidOperationException("One-screen webtoon not ready");
            await PageJumpSubmitAsync(reader, "1", enter: true);
            var scroll = reader.WebtoonView.FindName("Scroll") as ScrollViewer;
            bool fit = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && !PageJumpOpen(reader) && reader.WebtoonCurrentPage == 0);
            reader.TestFlushSave();
            checks.Add(("AC03/04 不足一屏条漫同页跳转", fit && scroll is { ScrollableHeight: <= 0.5 }
                && reader.WebtoonScrollOffset < 1 && App.Library.GetProgress(book.Id) is { PageIndex: 0, Mode: "webtoon", ScrollOffset: 0 }, PageJumpDetail(reader, book)));
        }
        catch (Exception ex) { checks.Add(("页码单页书组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task PageJumpPendingAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        OpcControlledSource? gate = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "页码原始pending意图", 6));
            App.Library.SaveProgress(book.Id, 1.0 / 6, 0, 0, 0, "paged", 1);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => PageJumpPaged(reader, 0))) throw new InvalidOperationException("Pending baseline not ready");
            gate = OpcInstallControlled(reader, book);
            gate.DelayedPage = 3;
            await PageJumpSubmitAsync(reader, "4");
            bool olderEntered = await WaitLogicAuditAsync(() => gate.Entered, 2);
            await PageJumpSubmitAsync(reader, "6", enter: true);
            gate.Release();
            bool latest = await WaitLogicAuditAsync(() => PageJumpPaged(reader, 5));
            await Task.Delay(150); // Drain the released older completion before observing saved facts.
            reader.TestFlushSave();
            checks.Add(("AC09 新入口慢B被C取代且旧完成不回灌", olderEntered && latest && PageJumpPaged(reader, 5)
                && OpcSaved(book, 5, "paged", 1), PageJumpDetail(reader, book)));
            await PageJumpSubmitAsync(reader, "1");
            if (!await WaitLogicAuditAsync(() => PageJumpPaged(reader, 0))) throw new InvalidOperationException("Pending reset did not reach A");
            gate = OpcInstallControlled(reader, book);
            gate.DelayedPage = 3;
            await PageJumpSubmitAsync(reader, "4");
            bool entered = await WaitLogicAuditAsync(() => gate.Entered, 2);
            await PageJumpSubmitAsync(reader, "0");
            bool rejected = PageJumpOpen(reader) && !string.IsNullOrEmpty(PageJumpControl<TextBlock>(reader, "PageJumpErrorText").Text);
            PageJumpClick(reader, "PageJumpCancelButton");
            reader.TestFlushSave();
            bool safe = OpcSaved(book, 0, "paged", 1.0 / 6);
            // The rejected draft must not cancel the valid raw index 3. Double
            // canonicalizes it to index 2, then webtoon must still inherit 3.
            reader.ModeDouble.IsChecked = true;
            reader.ModeWebtoon.IsChecked = true;
            gate.Release();
            bool carried = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 3
                && reader.WebtoonBlankStripCount == 0 && Math.Abs(reader.WebtoonScrollOffset - 3 * 600 * reader.WebtoonView.CanvasWidth / 240) <= 1);
            reader.TestFlushSave();
            checks.Add(("AC05/09 非法草稿不撤pending且跨模式保留原目标", entered && rejected && safe && carried
                && App.Library.GetProgress(book.Id) is { PageIndex: 3, Mode: "webtoon" }, PageJumpDetail(reader, book)));
            Capture(reader, Path.Combine(outDir, "page-jump-pending-original-target.png"));
            reader.ModePaged.IsChecked = true;
            if (!await WaitLogicAuditAsync(() => PageJumpPaged(reader, 3))) throw new InvalidOperationException("Pending close baseline not ready");
            gate = OpcInstallControlled(reader, book);
            gate.DelayedPage = 4;
            await PageJumpSubmitAsync(reader, "5");
            bool closingPending = await WaitLogicAuditAsync(() => gate.Entered, 2);
            reader.Close();
            gate.Release();
            checks.Add(("AC10 新入口pending关闭仍保存成功页4", closingPending && OpcSaved(book, 3, "paged", 4.0 / 6), OpcDb(book)));
        }
        catch (Exception ex) { checks.Add(("页码pending组异常", false, ex.ToString())); }
        finally { gate?.Release(); reader?.Close(); }
    }

    private static bool PageJumpColor(Image image, byte red, byte green, byte blue)
    {
        if (image.Source is not BitmapSource bitmap || bitmap.PixelWidth < 20 || bitmap.PixelHeight < 20) return false;
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
        return Math.Abs(pixel[2] - red) <= 2 && Math.Abs(pixel[1] - green) <= 2 && Math.Abs(pixel[0] - blue) <= 2;
    }

    private static async Task PageJumpLongAndPdfAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            string fixtureDir = Path.Combine(outDir, "page-jump-long");
            Directory.CreateDirectory(fixtureDir);
            var book = await OpcImportAsync(CreateLongComic(fixtureDir, 30, "page-jump-30.cbz"));
            App.Library.SaveProgress(book.Id, 2.0 / 30, 1, 0, 0, "paged", 1);
            reader = PageJumpReader(book);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 1)) throw new InvalidOperationException("30-page baseline not ready");
            bool shortcut = PageJumpCtrlG(reader);
            await PageJumpSubmitAsync(reader, "17", enter: true);
            bool distant = await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 16
                && reader.PageInfoText.Text == "17 / 30" && PageJumpColor(reader.PageImage, 149, 60, 200));
            reader.TestFlushSave();
            checks.Add(("AC01 30页漫画CtrlG从2远跳17像素DB", shortcut && distant && OpcSaved(book, 16, "paged", 17.0 / 30), PageJumpDetail(reader, book)));
            Capture(reader, Path.Combine(outDir, "page-jump-30-to-17.png"));
            reader.Close();

            var pdf = await OpcImportAsync(PageJumpTallPdf(outDir));
            App.Library.SaveProgress(pdf.Id, 1.0 / 3, 0, 0, 0, "paged", 1);
            reader = PageJumpReader(pdf);
            reader.Show();
            if (!await WaitLogicAuditAsync(() => reader.IsComicPageLoaded)) throw new InvalidOperationException("Sliced PDF baseline not ready");
            bool sliced = reader.ComicPageCount == 3 && PageJumpColor(reader.PageImage, 0, 0, 255);
            await PageJumpSubmitAsync(reader, "3", enter: true);
            bool logicalLast = await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 2
                && reader.PageInfoText.Text == "3 / 3" && PageJumpColor(reader.PageImage, 255, 0, 0));
            reader.TestFlushSave();
            checks.Add(("AC04 PDF单物理页三切片跳第3逻辑页", sliced && logicalLast && OpcSaved(pdf, 2, "paged", 1), PageJumpDetail(reader, pdf)));
            Capture(reader, Path.Combine(outDir, "page-jump-pdf-logical-slice.png"));
        }
        catch (Exception ex) { checks.Add(("页码长书与PDF组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static string PageJumpTallPdf(string outDir)
    {
        // One physical page, three visibly distinct 100x200 bands. PDF origin is
        // bottom-left: logical top slice is blue, final bottom slice is red.
        const string content = "1 0 0 rg 0 0 100 200 re f\n0 1 0 rg 0 200 100 200 re f\n0 0 1 rg 0 400 100 200 re f\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 600] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        text.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        string path = Path.Combine(outDir, "page-jump-one-page-three-slices.pdf");
        File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    private static async Task PageJumpNovelAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            string path = Path.Combine(outDir, "页码入口小说禁用.txt");
            await File.WriteAllTextAsync(path, "第一章 测试\n这是独立验收的小说正文，不应启用漫画页码跳转。\n");
            var book = await OpcImportAsync(path);
            reader = PageJumpReader(book);
            reader.Show();
            bool ready = await WaitLogicAuditAsync(() => reader.IsNovelReady);
            var entry = PageJumpControl<Button>(reader, "PageJumpButton");
            string before = reader.PageInfoText.Text;
            bool command = PageJumpCtrlG(reader);
            checks.Add(("AC07 小说漫画跳页入口与命令禁用", ready && !entry.IsEnabled && !entry.IsHitTestVisible
                && !command && !PageJumpOpen(reader) && reader.PageInfoText.Text == before, PageJumpDetail(reader, book)));
        }
        catch (Exception ex) { checks.Add(("页码小说禁用组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }
}
