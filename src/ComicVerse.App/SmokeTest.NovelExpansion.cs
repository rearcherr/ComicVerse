using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.App.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;

namespace ComicVerse.App;

/// <summary>独立 QA 回归：通过控件与真实文档状态验收，不改动用户书库或注入桌面输入。</summary>
public static partial class SmokeTest
{
    private static async Task<bool> WaitNovelExpansionAsync(Func<bool> condition, int seconds = 10)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) return true;
            await Task.Delay(75);
        }
        return condition();
    }

    private static Task<bool> WaitNovelExpansionLayoutAsync(ReaderWindow reader) =>
        WaitNovelExpansionAsync(() => reader.IsNovelReady && reader.IsNovelChapterLayoutStable);

    private static bool IsNovelPageStateConsistent(ReaderWindow reader)
        => reader.IsNovelChapterLayoutStable && reader.NovelDisplayedPageCount > 0
            && reader.NovelDisplayedPageNumber >= 1 && reader.NovelDisplayedPageNumber <= reader.NovelDisplayedPageCount
            && reader.NovelPage == reader.NovelDisplayedPageNumber - 1
            && reader.PageInfoText.Text.EndsWith($"第 {reader.NovelDisplayedPageNumber} 页", StringComparison.Ordinal);

    private static bool HasVisibleMinimumWindowContent(ReaderWindow reader, out string detail)
    {
        detail = "当前分页视觉尚未就绪";
        var pageView = reader.NovelPaged.PageViews.FirstOrDefault(p =>
            p.PageNumber == reader.NovelDisplayedPageNumber - 1 && p.DocumentPage != DocumentPage.Missing);
        if (pageView?.DocumentPage is not { } page || page.Visual is null
            || reader.NovelPaged.Document is not FlowDocument document) return false;
        try
        {
            // 直接测量 WPF 最终分页结果及视觉变换，不用声明的 PagePadding 推算净高。
            Rect content = page.ContentBox;
            Rect rendered = page.Visual.TransformToAncestor(reader.NovelPaged).TransformBounds(content);
            var viewport = new Rect(0, 0, reader.NovelPaged.ActualWidth, reader.NovelPaged.ActualHeight);
            viewport.Inflate(1, 1); // 允许设备像素取整误差。
            double lineHeight = double.IsFinite(document.LineHeight) ? document.LineHeight : document.FontSize;
            detail = $"page={page.Size}; content={content}; rendered={rendered}; viewport={viewport}; lineHeight={lineHeight}";
            return !content.IsEmpty && content.Width >= document.FontSize && content.Height >= lineHeight
                && !rendered.IsEmpty && viewport.Contains(rendered);
        }
        catch (InvalidOperationException) { return false; } // 旧分页视觉已在换布局时脱离。
    }

    private static async Task<Book> ImportNovelExpansionFixtureAsync(string outDir, string fileName, string text)
    {
        string file = Path.Combine(outDir, fileName);
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
        var imported = await App.Importer.ImportAsync([file]);
        if (imported.Failed.Count > 0) throw new InvalidOperationException(string.Join("; ", imported.Failed));
        return App.Library.GetBookByPath(file) ?? throw new InvalidOperationException("QA 样例导入后未出现在书库");
    }

    private static async Task<(bool Spacing, bool Search, string Detail)> CheckNovelExpansionAsync(string outDir)
    {
        var spacing = await CheckNovelSpacingExpansionAsync(outDir);
        var search = await CheckNovelSearchExpansionAsync(outDir);
        var longParagraph = await CheckLongParagraphSearchExpansionAsync(outDir);
        var epub = await CheckEpubSearchExpansionAsync(outDir);
        return (spacing.Ok, search.Ok && longParagraph.Ok && epub.Ok,
            spacing.Detail + " | " + search.Detail + " | " + longParagraph.Detail + " | " + epub.Detail);
    }

    private static async Task<(bool Ok, string Detail)> CheckNovelSpacingExpansionAsync(string outDir)
    {
        ReaderWindow? reader = null;
        var checks = new List<(string Name, bool Ok)>();
        try
        {
            var book = await ImportNovelExpansionFixtureAsync(outDir, "QA-精确排版.txt",
                "第1章 清晨\n\n" + string.Concat(Enumerable.Repeat("窗外亮起晨光，桌上的书翻开了新的一页。", 12))
                + "\n\n我们保留每一段文字的原样，检查排版是否准确。\n");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            checks.Add(("初始布局", await WaitNovelExpansionLayoutAsync(reader)));

            reader.TestSetNovelSpacingText(true, "48");
            reader.TestSetNovelSpacingText(false, "160");
            checks.Add(("新范围生效", await WaitNovelExpansionAsync(() =>
                Math.Abs(reader.NovelFormattingSnapshot.ParagraphSpacing - 48) < 0.01
                && Math.Abs(reader.NovelFormattingSnapshot.PageMargin - 160) < 0.01
                && Math.Abs(reader.NovelDocumentMargin - 160) < 0.01
                && reader.IsNovelChapterLayoutStable)));
            checks.Add(("数值显示", reader.NovelParagraphSpacingText == "48" && reader.NovelMarginText == "160"));

            reader.TestSetNovelSpacingText(true, "");
            checks.Add(("空值不写坏", reader.NovelParagraphSpacingMessage.Length > 0
                && Math.Abs(reader.NovelFormattingSnapshot.ParagraphSpacing - 48) < 0.01));
            reader.TestSetNovelSpacingText(true, "不是数字");
            checks.Add(("非法值不写坏", reader.NovelParagraphSpacingMessage.Length > 0
                && Math.Abs(reader.NovelFormattingSnapshot.ParagraphSpacing - 48) < 0.01));
            reader.TestSetNovelSpacingText(true, "-5");
            reader.TestSetNovelSpacingText(false, "999");
            checks.Add(("越界钳制", await WaitNovelExpansionAsync(() =>
                reader.NovelFormattingSnapshot.ParagraphSpacing == 0
                && reader.NovelFormattingSnapshot.PageMargin == 240
                && reader.NovelParagraphSpacingText == "0" && reader.NovelMarginText == "240")));

            reader.TestSetNovelSpacingText(true, "120");
            checks.Add(("段距上限", await WaitNovelExpansionAsync(() =>
                reader.NovelFormattingSnapshot.ParagraphSpacing == 120 && reader.IsNovelChapterLayoutStable)));
            reader.Width = 900;
            reader.ChaptersButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.SettingsPanelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.UpdateLayout();
            bool compactStable = await WaitNovelExpansionLayoutAsync(reader);
            double available = reader.NovelPaged.ActualWidth - reader.NovelDocumentMargin * 2;
            checks.Add(("窄窗双侧栏", compactStable && reader.NovelDisplayedPageCount > 0));
            checks.Add(("窄窗正文有可用宽度", available > 0));
            Capture(reader, Path.Combine(outDir, "qa-spacing-minwidth.png"));

            // 同时覆盖最小宽、高与最大边距；WPF 会调整实际分页，必须看渲染结果。
            reader.Height = 560;
            reader.UpdateLayout();
            checks.Add(("最小窗口实际尺寸", await WaitNovelExpansionLayoutAsync(reader)
                && reader.ActualWidth <= 900 && reader.ActualHeight <= 560));
            var minimumSearch = reader.TestOpenNovelSearch();
            await minimumSearch.SearchForTestAsync("检查排版是否准确");
            bool minimumPaged = minimumSearch.ResultCount == 1
                && await minimumSearch.NavigateResultForTestAsync(0);
            checks.Add(("最小窗口分页匹配定位", minimumPaged
                && reader.NovelSearchSelectedText == "检查排版是否准确"
                && reader.NovelDisplayedPageNumber == reader.NovelSearchLocatedPageIndex + 1));
            string minimumDetail = "";
            checks.Add(("最小窗口真实内容区可见", await WaitNovelExpansionAsync(() =>
                HasVisibleMinimumWindowContent(reader, out minimumDetail))));
            Capture(reader, Path.Combine(outDir, "qa-spacing-minsize-paged.png"));

            double smallTopPadding = (reader.NovelPaged.Document as FlowDocument)?.PagePadding.Top ?? double.NaN;
            int smallPageCount = reader.NovelDisplayedPageCount;
            reader.TestSliderDrag(0, 1);
            checks.Add(("小窗实际跳至末页", await WaitNovelExpansionAsync(() =>
                reader.IsNovelChapterLayoutStable && smallPageCount > 1
                && reader.NovelPage == smallPageCount - 1 && reader.NovelDisplayedPageNumber == smallPageCount)));
            reader.Height = 900;
            reader.UpdateLayout();
            await WaitNovelExpansionLayoutAsync(reader);
            string grownDetail = "";
            checks.Add(("放大窗口恢复较大实际边距", await WaitNovelExpansionAsync(() =>
                reader.NovelPaged.Document is FlowDocument enlarged && enlarged.PagePadding.Top > smallTopPadding + 0.5
                && reader.NovelFormattingSnapshot.PageMargin == 240
                && HasVisibleMinimumWindowContent(reader, out grownDetail))));
            double largeTopPadding = (reader.NovelPaged.Document as FlowDocument)?.PagePadding.Top ?? double.NaN;
            checks.Add(("页数收缩后游标一致", await WaitNovelExpansionAsync(() =>
                reader.IsNovelChapterLayoutStable && reader.NovelDisplayedPageCount < smallPageCount
                && reader.NovelDisplayedPageCount > 0 && reader.NovelPage == reader.NovelDisplayedPageCount - 1
                && reader.NovelDisplayedPageNumber == reader.NovelPage + 1)));
            int largePageCount = reader.NovelDisplayedPageCount, resizedModel = reader.NovelPage;
            int resizedDisplay = reader.NovelDisplayedPageNumber;
            reader.TestFlushSave();
            var resizedProgress = App.Library.GetProgress(book.Id);
            checks.Add(("页数收缩后保存真实游标", resizedProgress is not null && resizedProgress.ChapterIndex == 0
                && resizedProgress.PageIndex == reader.NovelPage && resizedProgress.PageIndex == largePageCount - 1));
            Capture(reader, Path.Combine(outDir, "qa-spacing-grown-paged.png"));
            reader.Height = 560;
            reader.UpdateLayout();
            await WaitNovelExpansionLayoutAsync(reader);
            string shrunkDetail = "";
            checks.Add(("缩小窗口重新形成可读版心", await WaitNovelExpansionAsync(() =>
                reader.NovelPaged.Document is FlowDocument reduced && reduced.PagePadding.Top < largeTopPadding - 0.5
                && reader.NovelFormattingSnapshot.PageMargin == 240
                && HasVisibleMinimumWindowContent(reader, out shrunkDetail))));
            bool reverseConsistent = await WaitNovelExpansionAsync(() =>
                reader.NovelDisplayedPageCount > largePageCount && IsNovelPageStateConsistent(reader));
            await Task.Delay(150);
            checks.Add(("反向页数增长游标一致", reverseConsistent && IsNovelPageStateConsistent(reader)
                && reader.NovelDisplayedPageCount > largePageCount));
            int reverseCount = reader.NovelDisplayedPageCount, reverseModel = reader.NovelPage;
            int reverseDisplay = reader.NovelDisplayedPageNumber;
            reader.TestFlushSave();
            var reverseProgress = App.Library.GetProgress(book.Id);
            checks.Add(("反向页数增长保存实际页", reverseProgress is not null && reverseProgress.ChapterIndex == 0
                && reverseProgress.PageIndex == reader.NovelDisplayedPageNumber - 1
                && reverseProgress.PageIndex == reader.NovelPage));
            Capture(reader, Path.Combine(outDir, "qa-spacing-reshrunk-paged.png"));

            // 旧模型页码仍小于新页数时，也不能漏掉 WPF 自动重排后的实际显示变化。
            reader.TestSliderDrag(0, 0.25);
            checks.Add(("有效旧游标场景准备", await WaitNovelExpansionAsync(() =>
                IsNovelPageStateConsistent(reader) && reader.NovelPage > 0
                && reader.NovelDisplayedPageNumber < reader.NovelDisplayedPageCount)));
            int validOldModel = reader.NovelPage, validOldDisplay = reader.NovelDisplayedPageNumber;
            int validOldCount = reader.NovelDisplayedPageCount;
            reader.Height = 900;
            reader.UpdateLayout();
            bool validReflow = await WaitNovelExpansionAsync(() =>
                reader.NovelDisplayedPageCount < validOldCount && validOldModel < reader.NovelDisplayedPageCount
                && IsNovelPageStateConsistent(reader));
            await Task.Delay(150);
            checks.Add(("旧游标未越界重排仍同步", validReflow && IsNovelPageStateConsistent(reader)
                && reader.NovelDisplayedPageCount < validOldCount && validOldModel < reader.NovelDisplayedPageCount));
            int validNewDisplay = reader.NovelDisplayedPageNumber, validNewCount = reader.NovelDisplayedPageCount;
            reader.TestFlushSave();
            var validProgress = App.Library.GetProgress(book.Id);
            checks.Add(("旧游标未越界保存实际页", validProgress is not null && validProgress.ChapterIndex == 0
                && validProgress.PageIndex == reader.NovelDisplayedPageNumber - 1
                && validProgress.PageIndex == reader.NovelPage));
            Capture(reader, Path.Combine(outDir, "qa-spacing-valid-model-resize.png"));

            // 原生查看器导航走 WPF 命令，不借用 Reader 自己的 Next/Previous 路径。
            int nativeBefore = reader.NovelDisplayedPageNumber;
            bool nativeNextAllowed = NavigationCommands.NextPage.CanExecute(null, reader.NovelPaged);
            if (nativeNextAllowed) NavigationCommands.NextPage.Execute(null, reader.NovelPaged);
            bool nativeNext = await WaitNovelExpansionAsync(() =>
                reader.NovelDisplayedPageNumber == nativeBefore + 1 && IsNovelPageStateConsistent(reader));
            reader.TestFlushSave();
            var nativeNextProgress = App.Library.GetProgress(book.Id);
            checks.Add(("原生下一页显示模型底栏存盘一致", nativeNextAllowed && nativeNext
                && nativeNextProgress?.PageIndex == reader.NovelDisplayedPageNumber - 1));
            bool nativePreviousAllowed = NavigationCommands.PreviousPage.CanExecute(null, reader.NovelPaged);
            if (nativePreviousAllowed) NavigationCommands.PreviousPage.Execute(null, reader.NovelPaged);
            bool nativePrevious = await WaitNovelExpansionAsync(() =>
                reader.NovelDisplayedPageNumber == nativeBefore && IsNovelPageStateConsistent(reader));
            reader.TestFlushSave();
            var nativePreviousProgress = App.Library.GetProgress(book.Id);
            checks.Add(("原生上一页显示模型底栏存盘一致", nativePreviousAllowed && nativePrevious
                && nativePreviousProgress?.PageIndex == reader.NovelDisplayedPageNumber - 1));
            Capture(reader, Path.Combine(outDir, "qa-spacing-native-navigation.png"));
            reader.Height = 560;
            reader.UpdateLayout();
            await WaitNovelExpansionLayoutAsync(reader);

            var largeType = reader.NovelFormattingSnapshot;
            largeType.FontSize = 32;
            largeType.LineSpacing = 2.5;
            reader.TestSetNovelFormatting(largeType);
            await WaitNovelExpansionLayoutAsync(reader);
            bool largeTypeNavigation = await minimumSearch.NavigateResultForTestAsync(0);
            string largeTypeDetail = "";
            checks.Add(("字号32行距2点5真实版心", await WaitNovelExpansionAsync(() =>
                largeTypeNavigation && reader.NovelSearchSelectedText == "检查排版是否准确"
                && reader.NovelPaged.Document is FlowDocument typed && typed.FontSize == 32 && typed.LineHeight == 80
                && HasVisibleMinimumWindowContent(reader, out largeTypeDetail))));
            Capture(reader, Path.Combine(outDir, "qa-spacing-font32-paged.png"));

            reader.NovelModeScroll.IsChecked = true;
            bool minimumScrollLayout = await WaitNovelExpansionLayoutAsync(reader);
            bool minimumScroll = minimumScrollLayout && await minimumSearch.NavigateResultForTestAsync(0);
            bool minimumScrollVisible = await WaitNovelExpansionAsync(() => reader.NovelSearchSelectionInViewport);
            checks.Add(("最小窗口滚动匹配整行可见", minimumScroll && minimumScrollVisible
                && reader.NovelSearchSelectedText == "检查排版是否准确"));
            checks.Add(("滚动恢复完整请求边距", reader.NovelScroll.Document is FlowDocument scrollDocument
                && scrollDocument.PagePadding.Top == 240 && scrollDocument.PagePadding.Bottom == 240
                && reader.NovelFormattingSnapshot.PageMargin == 240));
            Capture(reader, Path.Combine(outDir, "qa-spacing-minsize-scroll.png"));
            string minimumScrollDetail = reader.NovelSearchSelectionViewportDetail;
            minimumSearch.Close();
            reader.NovelModePaged.IsChecked = true;
            await WaitNovelExpansionLayoutAsync(reader);

            // 立即关闭仍保存请求值，而不是把为小窗临时收缩的上下 Padding 写回书库。
            reader.TestSetNovelSpacingText(false, "239");
            reader.TestSetNovelSpacingText(false, "240");
            reader.Close();
            reader = null;
            App.Settings = new AppSettingsService(App.Library);
            reader = new ReaderWindow(book) { ShowInTaskbar = false, Width = 900, Height = 560 };
            reader.Show();
            await WaitNovelExpansionLayoutAsync(reader);
            reader.SettingsPanelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.UpdateLayout();
            string reopenedDetail = "";
            checks.Add(("最大边距立即关闭小窗重开可读", await WaitNovelExpansionAsync(() =>
                reader.IsNovelChapterLayoutStable && reader.ActualWidth <= 900 && reader.ActualHeight <= 560
                && reader.NovelFormattingSnapshot.PageMargin == 240 && reader.NovelMarginText == "240"
                && reader.NovelPaged.Document is FlowDocument reopened && reopened.FontSize == 32 && reopened.LineHeight == 80
                && HasVisibleMinimumWindowContent(reader, out reopenedDetail))));
            Capture(reader, Path.Combine(outDir, "qa-spacing-minsize-reopened.png"));

            // 草稿不等于已应用值；直接关窗必须提交有效草稿，而不是静默丢失。
            reader.TestSetNovelSpacingText(true, "0", commit: false);
            reader.TestSetNovelSpacingText(false, "0", commit: false);
            reader.Close();
            reader = null;
            App.Settings = new AppSettingsService(App.Library);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            checks.Add(("零值草稿关窗恢复", await WaitNovelExpansionLayoutAsync(reader)
                && reader.NovelFormattingSnapshot.ParagraphSpacing == 0
                && reader.NovelFormattingSnapshot.PageMargin == 0 && reader.NovelDocumentMargin == 0
                && reader.NovelParagraphSpacingText == "0" && reader.NovelMarginText == "0"));
            reader.TestSetNovelSpacingText(false, "无效草稿", commit: false);
            reader.Close();
            reader = null;
            App.Settings = new AppSettingsService(App.Library);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            checks.Add(("非法草稿关闭保留旧值", await WaitNovelExpansionLayoutAsync(reader)
                && reader.NovelFormattingSnapshot.PageMargin == 0 && reader.NovelMarginText == "0"));
            string failed = string.Join(",", checks.Where(c => !c.Ok).Select(c => c.Name));
            return (checks.All(c => c.Ok), $"排版 {checks.Count(c => c.Ok)}/{checks.Count}（失败:{failed}; 窄窗内容净宽{available:F0}; "
                + $"最小窗口:{minimumDetail}; 实际上边距{smallTopPadding:F1}→{largeTopPadding:F1}→小窗; "
                + $"页数{smallPageCount}→{largePageCount},model/display/saved={resizedModel}/{resizedDisplay}/{resizedProgress?.PageIndex}; "
                + $"反向{largePageCount}→{reverseCount}:{reverseModel}/{reverseDisplay}/{reverseProgress?.PageIndex}; "
                + $"有效旧游标{validOldModel},页数{validOldCount}→{validNewCount},显示{validOldDisplay}→{validNewDisplay},存{validProgress?.PageIndex}; "
                + $"大字:{largeTypeDetail}; 重开:{reopenedDetail}; 滚动:{minimumScrollDetail}）");
        }
        catch (Exception ex)
        {
            Log.Error("独立 QA 排版扩展回归失败", ex);
            return (false, "排版异常:" + ex.Message);
        }
        finally { reader?.Close(); }
    }

    private static async Task<(bool Ok, string Detail)> CheckNovelSearchExpansionAsync(string outDir)
    {
        const string query = "定位标记[星河].*";
        const string prefix = "定位之前：";
        ReaderWindow? reader = null;
        NovelSearchWindow? search = null;
        var checks = new List<(string Name, bool Ok)>();
        try
        {
            var text = new StringBuilder();
            string common = string.Concat(Enumerable.Repeat("旅程向前延伸，海岸和山谷在窗外缓缓经过。", 6));
            for (int chapter = 1; chapter <= 2; chapter++)
            {
                text.Append($"第{chapter}章 远行{chapter}\n\n");
                for (int paragraph = 0; paragraph < 70; paragraph++)
                {
                    if (paragraph == (chapter == 1 ? 55 : 60))
                        text.Append(prefix).Append(query).Append("，这就是需要选中的词句。");
                    text.Append($"片段{paragraph}：").Append(common).Append("\n\n");
                }
            }
            var book = await ImportNovelExpansionFixtureAsync(outDir, "QA-跨章全文查找.txt", text.ToString());
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            checks.Add(("查找样例布局", await WaitNovelExpansionLayoutAsync(reader)));
            search = reader.TestOpenNovelSearch();
            checks.Add(("唯一搜索窗口", ReferenceEquals(search, reader.TestOpenNovelSearch())
                && reader.OwnedWindows.OfType<NovelSearchWindow>().Count() == 1));
            await search.SearchForTestAsync(query);
            checks.Add(("中文标点字面搜索", search.ResultCount == 2 && !search.IsTruncated));
            Capture(search, Path.Combine(outDir, "qa-novel-search-results.png"));
            bool second = await search.NavigateResultForTestAsync(1);
            checks.Add(("跨章精确选中", second && reader.NovelChapterIndex == 1
                && reader.NovelSearchSelectedText == query && reader.NovelSearchSelectionOffset == prefix.Length
                && reader.NovelSearchLocatedBlockIndex == 61));
            checks.Add(("实际后页定位", reader.NovelSearchLocatedPageIndex > 0
                && reader.NovelDisplayedPageNumber == reader.NovelSearchLocatedPageIndex + 1
                && reader.NovelDisplayedPageNumber == reader.NovelPage + 1));
            Capture(reader, Path.Combine(outDir, "qa-novel-search-paged.png"));
            reader.TestFlushSave();
            var progress = App.Library.GetProgress(book.Id);
            checks.Add(("搜索定位保存", progress is not null && progress.ChapterIndex == 1
                && progress.PageIndex == reader.NovelSearchLocatedPageIndex));

            // 已选结果没有 SelectionChanged：离开后再次单击同一行也必须重新定位。
            reader.TestNovelGoToChapter(0);
            await WaitNovelExpansionLayoutAsync(reader);
            search.ResultsList.ScrollIntoView(search.ResultsList.SelectedItem);
            search.ResultsList.UpdateLayout();
            if (search.ResultsList.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem selectedRow)
            {
                selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
                selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            }
            checks.Add(("已选结果单击再定位", await WaitNovelExpansionAsync(() =>
                reader.NovelChapterIndex == 1 && reader.NovelSearchSelectedText == query
                && reader.NovelSearchLocatedPageIndex > 0 && search.ResultsList.SelectedIndex == 1)));
            reader.TestNovelGoToChapter(0);
            await WaitNovelExpansionLayoutAsync(reader);
            search.ResultsList.Focus();
            var source = PresentationSource.FromVisual(search);
            if (source is not null)
            {
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                search.ResultsList.RaiseEvent(enter);
                if (!enter.Handled)
                {
                    enter.RoutedEvent = Keyboard.KeyDownEvent;
                    search.ResultsList.RaiseEvent(enter);
                }
            }
            checks.Add(("已选结果可重新激活", await WaitNovelExpansionAsync(() =>
                reader.NovelChapterIndex == 1 && reader.NovelSearchSelectedText == query
                && reader.NovelSearchLocatedPageIndex > 0 && search.ResultsList.SelectedIndex == 1)));

            // 上一处按钮应走同一搜索结果导航，而非只更新列表选中项。
            search.PreviousButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks.Add(("上一处真实导航", await WaitNovelExpansionAsync(() =>
                reader.NovelChapterIndex == 0 && reader.NovelSearchSelectedText == query
                && reader.NovelSearchLocatedBlockIndex == 56)));
            reader.NovelModeScroll.IsChecked = true;
            checks.Add(("滚动布局", await WaitNovelExpansionLayoutAsync(reader)));
            bool scrollLocated = await search.NavigateResultForTestAsync(1);
            checks.Add(("滚动结果定位", scrollLocated && reader.NovelChapterIndex == 1
                && reader.NovelSearchSelectedText == query && reader.NovelSearchSelectionOffset == prefix.Length
                && reader.NovelActualScrollFraction > 0.3));
            Capture(reader, Path.Combine(outDir, "qa-novel-search-scroll.png"));

            await search.SearchForTestAsync("");
            checks.Add(("空查询", search.ResultCount == 0 && search.StatusValue.Contains("请输入")));
            await search.SearchForTestAsync("__绝不会出现的词句_qa__");
            checks.Add(("无结果", search.ResultCount == 0 && search.StatusValue.Contains("未找到")));
            await search.SearchForTestAsync("旅程");
            checks.Add(("截断有提示", search.ResultCount == 500 && search.IsTruncated && search.StatusValue.Contains("前 500")));
            var stale = search.SearchForTestAsync("旅程");
            var current = search.SearchForTestAsync(query);
            await Task.WhenAll(stale, current);
            checks.Add(("旧查询不覆盖", search.ResultCount == 2 && !search.IsTruncated));
            var pendingNavigation = search.NavigateResultForTestAsync(0);
            reader.Close();
            reader = null;
            bool closedResult = await pendingNavigation;
            checks.Add(("关窗取消清理", !search.IsVisible && !closedResult));
            search = null;
            string failed = string.Join(",", checks.Where(c => !c.Ok).Select(c => c.Name));
            return (checks.All(c => c.Ok), $"查找 {checks.Count(c => c.Ok)}/{checks.Count}（失败:{failed}）");
        }
        catch (Exception ex)
        {
            Log.Error("独立 QA 正文查找回归失败", ex);
            return (false, "查找异常:" + ex.Message);
        }
        finally
        {
            search?.Close();
            reader?.Close();
        }
    }

    private static async Task<(bool Ok, string Detail)> CheckLongParagraphSearchExpansionAsync(string outDir)
    {
        const string query = "长段末部精确定位[星光]";
        ReaderWindow? reader = null;
        NovelSearchWindow? search = null;
        try
        {
            string before = string.Concat(Enumerable.Repeat("山风吹过原野，列车沿着海岸继续行驶。", 1200));
            string after = string.Concat(Enumerable.Repeat("这一段在目标词句之后，确保选中行不会只靠文末碰巧可见。", 60));
            var book = await ImportNovelExpansionFixtureAsync(outDir, "QA-超长单段落查找.txt",
                "第1章 无断行长段落\n\n" + before + query + after + "\n");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            if (!await WaitNovelExpansionAsync(() => reader.IsNovelReady)) return (false, "长段未加载");
            reader.NovelModeScroll.IsChecked = true;
            if (!await WaitNovelExpansionLayoutAsync(reader)) return (false, "长段滚动布局未稳定");
            search = reader.TestOpenNovelSearch();
            await search.SearchForTestAsync(query);
            bool unique = search.ResultCount == 1 && !search.IsTruncated;
            bool navigated = unique && await search.NavigateResultForTestAsync(0);
            bool visible = await WaitNovelExpansionAsync(() => reader.NovelSearchSelectionInViewport);
            bool exact = reader.NovelSearchSelectedText == query
                && reader.NovelSearchSelectionOffset == before.Length && reader.NovelSearchLocatedBlockIndex == 1;
            bool movedToTail = reader.NovelActualScrollFraction > 0.7;
            Capture(reader, Path.Combine(outDir, "qa-long-paragraph-search.png"));
            return (navigated && visible && exact && movedToTail,
                $"超长段选区={exact},真实视口={visible},滚动={reader.NovelActualScrollFraction:F2},导航={navigated}; "
                + Truncate(reader.NovelSearchSelectionViewportDetail, 230));
        }
        catch (Exception ex)
        {
            Log.Error("独立 QA 超长单段查找回归失败", ex);
            return (false, "超长段查找异常:" + ex.Message);
        }
        finally
        {
            search?.Close();
            reader?.Close();
        }
    }

    private static string CreateEpubSearchFixture(string outDir, string query, string prefix)
    {
        string path = Path.Combine(outDir, "QA-图文跨章搜索.epub");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void WriteEntry(string name, string content, CompressionLevel compression = CompressionLevel.Optimal)
        {
            using var stream = zip.CreateEntry(name, compression).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
        // 独立生成 EPUB 3 容器；mimetype 必须是首项且不压缩。
        WriteEntry("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        WriteEntry("META-INF/container.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        WriteEntry("OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="qa-id" xml:lang="zh-CN">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="qa-id">urn:uuid:c2ae07d8-602c-443a-b087-b357bb050405</dc:identifier>
                <dc:title>QA EPUB图文跨章搜索</dc:title><dc:language>zh-CN</dc:language><dc:creator>独立QA</dc:creator>
                <meta property="dcterms:modified">2026-11-01T00:00:00Z</meta>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
                <item id="qa-image" href="images/qa.png" media-type="image/png"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """);
        WriteEntry("OEBPS/nav.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="zh-CN">
              <head><title>目录</title></head><body><nav epub:type="toc" id="toc"><h1>目录</h1><ol>
                <li><a href="chapter1.xhtml">EPUB第1章 出海</a></li><li><a href="chapter2.xhtml">EPUB第2章 归航</a></li>
              </ol></nav></body>
            </html>
            """);
        string filler = string.Concat(Enumerable.Repeat("帆船沿着海岸行驶，晨光穿过云层洒在水面，远方传来海鸟的鸣声。", 6));
        for (int chapter = 1; chapter <= 2; chapter++)
        {
            string title = chapter == 1 ? "EPUB第1章 出海" : "EPUB第2章 归航";
            var xhtml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"zh-CN\"><head>"
                + "<title>不能出现在正文的元数据</title></head><body><h1>" + title
                + "</h1>\n<p><img src=\"images/qa.png\" alt=\"QA图示\"/></p>\n");
            for (int paragraph = 0; paragraph < 70; paragraph++)
            {
                xhtml.Append("<p>");
                if (chapter == 2 && paragraph == 60) xhtml.Append(prefix).Append(query).Append("，这才是目标正文。");
                xhtml.Append($"段落{paragraph}：").Append(filler).Append("</p>\n");
            }
            xhtml.Append("</body></html>");
            WriteEntry($"OEBPS/chapter{chapter}.xhtml", xhtml.ToString());
        }
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 96, 64));
        var bitmap = new RenderTargetBitmap(96, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var encodedImage = new MemoryStream())
        {
            png.Save(encodedImage); // WPF 编码器需要可 Seek 流，ZIP entry 写流不支持。
            encodedImage.Position = 0;
            using var stream = zip.CreateEntry("OEBPS/images/qa.png").Open();
            encodedImage.CopyTo(stream);
        }
        return path;
    }

    private static async Task<(bool Ok, string Detail)> CheckEpubSearchExpansionAsync(string outDir)
    {
        const string query = "EPUB定位[星桥].*";
        const string prefix = "图文之后🚆：";
        ReaderWindow? reader = null;
        NovelSearchWindow? search = null;
        var checks = new List<(string Name, bool Ok)>();
        try
        {
            string path = CreateEpubSearchFixture(outDir, query, prefix);
            var import = await App.Importer.ImportAsync([path]);
            if (import.Failed.Count != 0) throw new InvalidOperationException(string.Join("; ", import.Failed));
            var book = App.Library.GetBookByPath(path) ?? throw new InvalidOperationException("EPUB样例导入后缺失");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            checks.Add(("EPUB两章布局", await WaitNovelExpansionLayoutAsync(reader)
                && reader.NovelChapterCount == 2 && reader.NovelChapterIndex == 0));
            reader.TestSetNovelSpacingText(true, "8");
            reader.TestSetNovelSpacingText(false, "40");
            await WaitNovelExpansionLayoutAsync(reader);
            var firstDocument = reader.NovelPaged.Document as FlowDocument;
            var image = firstDocument?.Blocks.OfType<BlockUIContainer>().Select(b => b.Child)
                .OfType<Border>().Select(b => b.Child).OfType<Image>().FirstOrDefault();
            checks.Add(("EPUB前置图文渲染", image?.Source is BitmapSource source && source.PixelWidth == 96
                && source.PixelHeight == 64 && firstDocument is not null
                && new TextRange(firstDocument.ContentStart, firstDocument.ContentEnd).Text.Contains("EPUB第1章 出海")
                && !new TextRange(firstDocument.ContentStart, firstDocument.ContentEnd).Text.Contains("不能出现在正文的元数据")));
            Capture(reader, Path.Combine(outDir, "qa-epub-initial.png"));
            search = reader.TestOpenNovelSearch();
            await search.SearchForTestAsync(query);
            checks.Add(("EPUB字面搜索", search.ResultCount == 1 && !search.IsTruncated));
            Capture(search, Path.Combine(outDir, "qa-epub-search-results.png"));
            bool paged = await search.NavigateResultForTestAsync(0);
            checks.Add(("EPUB跨章偏移精确", paged && reader.NovelChapterIndex == 1
                && reader.NovelSearchSelectedText == query && reader.NovelSearchSelectionOffset == prefix.Length
                && reader.NovelSearchLocatedBlockIndex == 62)); // 标题 + 图片在 60 个正文块之前。
            checks.Add(("EPUB实际后页定位", reader.NovelSearchLocatedPageIndex > 0
                && reader.NovelDisplayedPageNumber == reader.NovelSearchLocatedPageIndex + 1
                && reader.NovelDisplayedPageNumber == reader.NovelPage + 1));
            string pageDetail = "";
            checks.Add(("EPUB目标页真实内容可见", await WaitNovelExpansionAsync(() =>
                HasVisibleMinimumWindowContent(reader, out pageDetail))));
            Capture(reader, Path.Combine(outDir, "qa-epub-search-paged.png"));
            int locatedPage = reader.NovelDisplayedPageNumber;
            reader.TestFlushSave();
            var progress = App.Library.GetProgress(book.Id);
            checks.Add(("EPUB定位进度保存", progress is not null && progress.ChapterIndex == 1
                && progress.PageIndex == reader.NovelSearchLocatedPageIndex));
            reader.NovelModeScroll.IsChecked = true;
            bool scrollLayout = await WaitNovelExpansionLayoutAsync(reader);
            bool scroll = scrollLayout && await search.NavigateResultForTestAsync(0);
            bool visible = await WaitNovelExpansionAsync(() => reader.NovelSearchSelectionInViewport);
            checks.Add(("EPUB滚动选区真实可见", scroll && visible && reader.NovelChapterIndex == 1
                && reader.NovelSearchSelectedText == query && reader.NovelSearchSelectionOffset == prefix.Length
                && reader.NovelSearchLocatedBlockIndex == 62 && reader.NovelActualScrollFraction > 0.5));
            Capture(reader, Path.Combine(outDir, "qa-epub-search-scroll.png"));
            string failed = string.Join(",", checks.Where(c => !c.Ok).Select(c => c.Name));
            return (checks.All(c => c.Ok), $"EPUB UI {checks.Count(c => c.Ok)}/{checks.Count}（失败:{failed}; "
                + $"实际页{locatedPage}; 图文后block={reader.NovelSearchLocatedBlockIndex}; offset={reader.NovelSearchSelectionOffset}; "
                + Truncate(reader.NovelSearchSelectionViewportDetail, 180) + "）");
        }
        catch (Exception ex)
        {
            Log.Error("独立 QA EPUB正文查找回归失败", ex);
            return (false, "EPUB UI异常:" + ex.Message);
        }
        finally
        {
            search?.Close();
            reader?.Close();
        }
    }
}
