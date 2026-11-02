using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.App.Helpers;
using ComicVerse.App.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;
using Microsoft.Data.Sqlite;

namespace ComicVerse.App;

/// <summary>独立 QA：跨功能状态对抗回归，所有 fixture 与书库均位于隔离测试目录。</summary>
public static partial class SmokeTest
{
    private static async Task<(bool Ok, string Detail)> CheckLogicAuditAsync(MainWindow main, string outDir)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        await CheckMixedDoubleLogicAuditAsync(outDir, checks);
        await CheckRapidComicModeLogicAuditAsync(outDir, checks);
        await CheckDoubleBoundaryLogicAuditAsync(outDir, checks);
        await CheckSingleWebtoonAutosaveLogicAuditAsync(outDir, checks);
        await CheckNovelModePositionLogicAuditAsync(outDir, checks);
        await CheckAutoScrollInputLogicAuditAsync(outDir, checks);
        await CheckBookmarkDeleteLogicAuditAsync(outDir, checks);
        await CheckNovelEndIntentLogicAuditAsync(outDir, checks);
        await CheckReaderSettingsRetryLogicAuditAsync(outDir, checks);
        await CheckEarlyReaderStateLogicAuditAsync(outDir, checks);
        string detail = string.Join(" | ", checks.Select(c => $"{c.Name}={c.Ok}({c.Detail})"));
        await File.WriteAllTextAsync(Path.Combine(outDir, "logic-audit-details.json"),
            JsonSerializer.Serialize(checks.Select(c => new { c.Name, c.Ok, c.Detail }),
                new JsonSerializerOptions { WriteIndented = true }));
        return (checks.All(c => c.Ok), $"逻辑审查 {checks.Count(c => c.Ok)}/{checks.Count} — {detail}");
    }

    private static async Task<bool> WaitLogicAuditAsync(Func<bool> condition, int seconds = 10)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) return true;
            await Task.Delay(60);
        }
        return condition();
    }

    private static async Task<Book> ImportLogicAuditComicAsync(string outDir, string name,
        params (int Width, int Height)[] dimensions)
    {
        string path = Path.Combine(outDir, name + ".cbz");
        using (var zipFile = File.Create(path))
        using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create))
        {
            for (int index = 0; index < dimensions.Length; index++)
            {
                var (width, height) = dimensions[index];
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(50 + index * 25), 90, 170)), null,
                        new Rect(0, 0, width, height));
                    dc.DrawRectangle(Brushes.White, null, new Rect(10, 10, Math.Min(width - 20, 70 + index * 15), 30));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var memory = new MemoryStream();
                encoder.Save(memory);
                memory.Position = 0;
                using var entry = zip.CreateEntry($"p{index:000}.png").Open();
                memory.CopyTo(entry);
            }
        }
        var imported = await App.Importer.ImportAsync([path]);
        if (imported.Failed.Count != 0) throw new InvalidOperationException(string.Join("; ", imported.Failed));
        return App.Library.GetBookByPath(path) ?? throw new InvalidOperationException("QA 漫画导入后缺失");
    }

    private static async Task<bool> WaitDoubleLogicAuditAsync(ReaderWindow reader, int? left = null, int? right = null)
        => await WaitLogicAuditAsync(() => reader.IsDoubleReady && reader.LeftImage.Source is not null
            && reader.DoubleLoading.Visibility == Visibility.Collapsed
            && (left is null || reader.DoubleLeftPageIndex == left)
            && (right is null || reader.DoubleRightPageIndex == right));

    private static string DoubleLogicAuditDetail(ReaderWindow reader)
        => $"start={reader.CurrentSpreadStart}; left={reader.DoubleLeftPageIndex}; right={reader.DoubleRightPageIndex}; "
            + $"leftPixels={(reader.LeftImage.Source as BitmapSource)?.PixelWidth}x{(reader.LeftImage.Source as BitmapSource)?.PixelHeight}; "
            + $"rightPixels={(reader.RightImage.Source as BitmapSource)?.PixelWidth}x{(reader.RightImage.Source as BitmapSource)?.PixelHeight}";

    private static bool LogicAuditPagePixelsMatch(Image image, int page)
    {
        if (image.Source is not BitmapSource source || source.PixelWidth < 11 || source.PixelHeight < 101) return false;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(10, 100, 1, 1), pixel, 4, 0);
        return pixel[0] == 170 && pixel[1] == 90 && pixel[2] == (byte)(50 + page * 25);
    }

    private static async Task CheckMixedDoubleLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditComicAsync(outDir, "QA-竖竖横竖竖",
                (300, 700), (300, 700), (1000, 600), (300, 700), (300, 700));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded || reader.IsWebtoonReady);
            reader.TestSwitchToPaged();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded);
            reader.TestSetRtl(false);
            reader.TestSwitchToDouble();
            bool loaded = await WaitDoubleLogicAuditAsync(reader, 0, 1);
            checks.Add(("混合双页首组0-1", loaded && reader.DoubleLeftPageIndex == 0 && reader.DoubleRightPageIndex == 1,
                DoubleLogicAuditDetail(reader)));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            loaded = await WaitDoubleLogicAuditAsync(reader, 2, -1);
            checks.Add(("横页2应独占", loaded && reader.CurrentSpreadStart == 2 && reader.DoubleLeftPageIndex == 2
                && reader.DoubleRightPageIndex == -1, DoubleLogicAuditDetail(reader)));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            loaded = await WaitDoubleLogicAuditAsync(reader, 3, 4);
            checks.Add(("后组3-4", loaded && reader.DoubleLeftPageIndex == 3 && reader.DoubleRightPageIndex == 4,
                DoubleLogicAuditDetail(reader)));
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            loaded = await WaitDoubleLogicAuditAsync(reader, 2, -1);
            checks.Add(("3-4回退应到独占2", loaded && reader.CurrentSpreadStart == 2
                && reader.DoubleLeftPageIndex == 2 && reader.DoubleRightPageIndex == -1, DoubleLogicAuditDetail(reader)));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-prev-from-3.png"));

            // 单独从横页回退，不能让上一个失败结果污染这一条路径。
            reader.TestSwitchToPaged();
            reader.TestJumpToPage(2);
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 2);
            reader.TestSwitchToDouble();
            await WaitDoubleLogicAuditAsync(reader, 2, -1);
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            loaded = await WaitDoubleLogicAuditAsync(reader, 0, 1);
            checks.Add(("独占2回退应到0-1", loaded && reader.CurrentSpreadStart == 0
                && reader.DoubleLeftPageIndex == 0 && reader.DoubleRightPageIndex == 1, DoubleLogicAuditDetail(reader)));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-prev-from-wide.png"));
            reader.Close();
            reader = null;

            var adjacent = await ImportLogicAuditComicAsync(outDir, "QA-竖横相邻", (300, 700), (1000, 600), (300, 700), (300, 700));
            reader = new ReaderWindow(adjacent) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded || reader.IsWebtoonReady);
            reader.TestSwitchToPaged();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded);
            reader.TestSetRtl(false);
            reader.TestSwitchToDouble();
            loaded = await WaitDoubleLogicAuditAsync(reader, 0, -1);
            checks.Add(("竖横相邻不合并", loaded && reader.DoubleLeftPageIndex == 0 && reader.DoubleRightPageIndex == -1,
                DoubleLogicAuditDetail(reader)));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-portrait-landscape.png"));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            loaded = await WaitDoubleLogicAuditAsync(reader, 1, -1);
            checks.Add(("竖横相邻下一组不跳横页", loaded && reader.CurrentSpreadStart == 1
                && reader.DoubleLeftPageIndex == 1 && reader.DoubleRightPageIndex == -1, DoubleLogicAuditDetail(reader)));
        }
        catch (Exception ex)
        {
            checks.Add(("混合双页异常", false, ex.ToString()));
            Log.Error("独立 QA 混合双页测试异常", ex);
        }
        finally { reader?.Close(); }
    }

    private static async Task CheckRapidComicModeLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var cold = await ImportLogicAuditComicAsync(outDir, "QA-首次条漫模式竞态",
                Enumerable.Range(0, 12).Select(i => (310 + i, 710 + i)).ToArray());
            App.Library.SaveProgress(cold.Id, 0, 0, 0, 0, "paged", 1);
            reader = new ReaderWindow(cold) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded);
            // 不 await 初次 Initialize：同一 UI 回合形成旧初始化与最新 mode intent 的交叉。
            reader.TestSwitchToWebtoon();
            reader.TestSwitchToPaged();
            reader.TestJumpToPage(8);
            reader.TestSwitchToDouble();
            reader.TestSwitchToWebtoon();
            bool finalWebtoon = await WaitLogicAuditAsync(() => reader.IsWebtoonReady
                && reader.WebtoonView.CurrentPage == 8 && reader.WebtoonRenderedCount > 0
                && reader.WebtoonBlankStripCount == 0);
            await Task.Delay(350);
            reader.TestFlushSave();
            var coldSaved = App.Library.GetProgress(cold.Id);
            checks.Add(("首次条漫初始化旧回调不覆盖最新模式位置", finalWebtoon && reader.ModeWebtoon.IsChecked == true
                && reader.WebtoonView.CurrentPage == 8 && reader.WebtoonVisibleRange.First <= 8 && reader.WebtoonVisibleRange.Last >= 8
                && coldSaved is { Mode: "webtoon", PageIndex: 8 }
                && Math.Abs(coldSaved.ScrollOffset - reader.WebtoonView.ScrollFraction) < 0.02,
                $"actual={reader.WebtoonView.CurrentPage}; range={reader.WebtoonVisibleRange}; saved={coldSaved?.Mode}/{coldSaved?.PageIndex}/{coldSaved?.ScrollOffset:F3}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-first-webtoon-mode-race.png"));
            reader.Close();
            reader = null;

            var mixed = App.Library.GetBookByPath(Path.Combine(outDir, "QA-竖竖横竖竖.cbz"))
                ?? throw new InvalidOperationException("快速模式用例缺少原混合漫画fixture");
            App.Library.SaveProgress(mixed.Id, 0, 0, 0, 0, "paged", 1);
            reader = new ReaderWindow(mixed) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded);
            reader.TestSetRtl(false);
            reader.TestSwitchToDouble();
            await WaitDoubleLogicAuditAsync(reader, 0, 1);
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool rapidNext = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && reader.CurrentSpreadStart == 3 && reader.DoubleLeftPageIndex == 3 && reader.DoubleRightPageIndex == 4
                && LogicAuditPagePixelsMatch(reader.LeftImage, 3) && LogicAuditPagePixelsMatch(reader.RightImage, 4));
            reader.TestFlushSave();
            var nextSaved = App.Library.GetProgress(mixed.Id);
            checks.Add(("快速双页Next两次按逻辑跨页前进", rapidNext && nextSaved is { Mode: "double", PageIndex: 3 },
                DoubleLogicAuditDetail(reader) + $"; DB={nextSaved?.Mode}/{nextSaved?.PageIndex}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-rapid-next.png"));

            reader.TestSwitchToPaged();
            reader.TestJumpToPage(3);
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 3);
            reader.TestSwitchToDouble();
            await WaitDoubleLogicAuditAsync(reader, 3, 4);
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool rapidPrevious = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && reader.CurrentSpreadStart == 0 && reader.DoubleLeftPageIndex == 0 && reader.DoubleRightPageIndex == 1
                && LogicAuditPagePixelsMatch(reader.LeftImage, 0) && LogicAuditPagePixelsMatch(reader.RightImage, 1));
            reader.TestFlushSave();
            var previousSaved = App.Library.GetProgress(mixed.Id);
            checks.Add(("快速双页Prev两次按逻辑跨页回退", rapidPrevious && previousSaved is { Mode: "double", PageIndex: 0 },
                DoubleLogicAuditDetail(reader) + $"; DB={previousSaved?.Mode}/{previousSaved?.PageIndex}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-rapid-prev.png"));

            reader.TestSwitchToPaged();
            reader.TestJumpToPage(0);
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 0);
            reader.TestSwitchToDouble();
            await WaitDoubleLogicAuditAsync(reader, 0, 1);
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.TestSwitchToPaged();
            reader.TestSliderDrag(0, 1); // 最后一次显式意图：翻页模式末页，不依赖 Next 是否已完成。
            bool paged = await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 4
                && reader.ModePaged.IsChecked == true && reader.DoubleView.Visibility == Visibility.Collapsed
                && LogicAuditPagePixelsMatch(reader.PageImage, 4));
            await Task.Delay(350);
            reader.TestFlushSave();
            var pagedSaved = App.Library.GetProgress(mixed.Id);
            checks.Add(("Next未落地切翻页后旧队列不抢模式", paged && reader.ModePaged.IsChecked == true
                && reader.ComicPagedView.Visibility == Visibility.Visible && reader.DoubleView.Visibility == Visibility.Collapsed
                && reader.CurrentPageNumber == 4 && LogicAuditPagePixelsMatch(reader.PageImage, 4)
                && pagedSaved is { Mode: "paged", PageIndex: 4 },
                $"actualPage={reader.CurrentPageNumber}; pagedVisible={reader.ComicPagedView.Visibility}; double={reader.DoubleView.Visibility}; DB={pagedSaved?.Mode}/{pagedSaved?.PageIndex}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-rapid-mode-paged.png"));
        }
        catch (Exception ex) { checks.Add(("快速漫画模式测试异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static bool LogicAuditDoubleShows(ReaderWindow reader, int left, int right)
        => reader.IsDoubleReady && reader.ModeDouble.IsChecked == true && reader.DoubleView.Visibility == Visibility.Visible
            && reader.DoubleLoading.Visibility == Visibility.Collapsed && reader.DoubleLeftPageIndex == left
            && reader.DoubleRightPageIndex == right && LogicAuditPagePixelsMatch(reader.LeftImage, left)
            && (right >= 0 ? LogicAuditPagePixelsMatch(reader.RightImage, right) : reader.RightImage.Source is null);

    private static async Task CheckDoubleBoundaryLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditComicAsync(outDir, "QA-奇数竖页与边界队列",
                (300, 700), (300, 700), (300, 700), (1000, 600), (300, 700), (300, 700));
            App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "paged", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded);
            reader.TestSetRtl(false);
            reader.TestSwitchToDouble();
            bool forward = await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, 0, 1));
            var forwardDetails = new List<string> { DoubleLogicAuditDetail(reader) };
            foreach (var expected in new[] { (2, -1), (3, -1), (4, 5) })
            {
                reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                forward &= await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, expected.Item1, expected.Item2));
                forwardDetails.Add(DoubleLogicAuditDetail(reader));
            }
            checks.Add(("奇数竖页run正向分组", forward, string.Join(" -> ", forwardDetails)));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            reader.TestFlushSave();
            var terminal = App.Library.GetProgress(book.Id);
            double terminalPercent = App.Library.GetBook(book.Id)?.Progress ?? -1;
            checks.Add(("完整末对Next幂等且可读完", LogicAuditDoubleShows(reader, 4, 5)
                && terminal is { Mode: "double", PageIndex: 4 } && Math.Abs(terminalPercent - 1) < 0.0001,
                DoubleLogicAuditDetail(reader) + $"; DB={terminal?.PageIndex}; progress={terminalPercent:F4}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-terminal-pair.png"));

            bool reverse = true;
            var reverseDetails = new List<string>();
            foreach (var expected in new[] { (3, -1), (2, -1), (0, 1) })
            {
                reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                reverse &= await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, expected.Item1, expected.Item2));
                reverseDetails.Add(DoubleLogicAuditDetail(reader));
            }
            checks.Add(("奇数竖页run反向回原分组", reverse, string.Join(" -> ", reverseDetails)));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-odd-reverse.png"));
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool headOrder = await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, 2, -1));
            reader.TestFlushSave();
            var headSaved = App.Library.GetProgress(book.Id);
            checks.Add(("页首Prev再Next不可净抵消", headOrder && headSaved is { Mode: "double", PageIndex: 2 },
                DoubleLogicAuditDetail(reader) + $"; DB={headSaved?.PageIndex}"));

            reader.TestSwitchToPaged();
            reader.TestJumpToPage(4);
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && reader.CurrentPageNumber == 4);
            reader.TestSwitchToDouble();
            await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, 4, 5));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool tailOrder = await WaitLogicAuditAsync(() => LogicAuditDoubleShows(reader, 3, -1));
            reader.TestFlushSave();
            var tailSaved = App.Library.GetProgress(book.Id);
            checks.Add(("末对Next再Prev不可净抵消", tailOrder && tailSaved is { Mode: "double", PageIndex: 3 },
                DoubleLogicAuditDetail(reader) + $"; DB={tailSaved?.PageIndex}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-double-boundary-order.png"));
        }
        catch (Exception ex) { checks.Add(("双页奇数与边界测试异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task CheckSingleWebtoonAutosaveLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditComicAsync(outDir, "QA-单张超长条漫自动保存", (400, 12000));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.IsComicPageLoaded || reader.IsWebtoonReady);
            reader.TestSwitchToWebtoon();
            bool ready = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonPageCount == 1);
            reader.WebtoonView.ScrollToFraction(0);
            await WaitLogicAuditAsync(() => reader.WebtoonScrollOffset < 1);
            reader.TestFlushSave(); // 只建立已知起点；滚动后不调用保存或关闭。
            await Task.Delay(2400); // 排除初始加载遗留的2秒保存计时器。
            var baseline = App.Library.GetProgress(book.Id);
            reader.WebtoonView.ScrollToFraction(0.65);
            bool moved = await WaitLogicAuditAsync(() => reader.WebtoonView.ScrollFraction > 0.6
                && reader.WebtoonView.CurrentPage == 0);
            double actual = reader.WebtoonView.ScrollFraction;
            await Task.Delay(2600);
            var automatic = App.Library.GetProgress(book.Id);
            checks.Add(("单张长条漫页内自动保存", ready && moved && baseline is not null && automatic is not null
                && automatic.PageIndex == 0 && Math.Abs(automatic.ScrollOffset - actual) < 0.02
                && Math.Abs(automatic.ScrollOffset - baseline.ScrollOffset) > 0.5,
                $"open={reader.IsVisible}; page={reader.WebtoonView.CurrentPage}; actual={actual:F4}; "
                + $"beforeDB={baseline?.ScrollOffset:F4}; after2.6sDB={automatic?.ScrollOffset:F4}; updated={automatic?.UpdatedAt:o}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-single-webtoon-autosave.png"));
        }
        catch (Exception ex)
        {
            checks.Add(("单张条漫自动保存异常", false, ex.ToString()));
            Log.Error("独立 QA 单张条漫自动保存测试异常", ex);
        }
        finally { reader?.Close(); }
    }

    private static async Task<Book> ImportLogicAuditNovelAsync(string outDir, string name)
    {
        var content = new StringBuilder();
        string body = string.Concat(Enumerable.Repeat("海风吹过灯塔，远处的列车经过山谷，读者沿着文字继续前行。", 6));
        for (int chapter = 1; chapter <= 3; chapter++)
        {
            content.Append($"第{chapter}章 {name}逻辑旅程{chapter}\n\n"); // 每组真实内容不同，避免导入器正确去重。
            for (int paragraph = 0; paragraph < 80; paragraph++)
                content.Append($"段落{paragraph}：").Append(body).Append("\n\n");
        }
        string path = Path.Combine(outDir, name + ".txt");
        await File.WriteAllTextAsync(path, content.ToString(), new UTF8Encoding(false));
        var imported = await App.Importer.ImportAsync([path]);
        if (imported.Failed.Count != 0) throw new InvalidOperationException(string.Join("; ", imported.Failed));
        return App.Library.GetBookByPath(path) ?? throw new InvalidOperationException("QA 小说导入后缺失");
    }

    private static Task<bool> WaitNovelLogicAuditAsync(ReaderWindow reader)
        => WaitLogicAuditAsync(() => reader.IsNovelReady && reader.IsNovelChapterLayoutStable);

    private static async Task CheckNovelModePositionLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditNovelAsync(outDir, "QA-小说双模式保位");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitNovelLogicAuditAsync(reader);
            reader.TestSliderDrag(0, 0.55 / reader.NovelChapterCount);
            await WaitNovelLogicAuditAsync(reader);
            int previousPage = reader.NovelPage, pages = reader.NovelDisplayedPageCount;
            double previousFraction = (double)previousPage / Math.Max(1, pages - 1);
            reader.NovelModeScroll.IsChecked = true;
            bool scrollReady = await WaitLogicAuditAsync(() => reader.IsNovelChapterLayoutStable
                && reader.NovelActualScrollFraction > 0.2
                && Math.Abs(reader.NovelActualScrollFraction - previousFraction) <= 0.18);
            double actualScroll = reader.NovelActualScrollFraction;
            checks.Add(("小说翻页后页切滚动保位", scrollReady && pages >= 6 && previousPage > 1
                && actualScroll > 0.2 && Math.Abs(actualScroll - previousFraction) <= 0.18,
                $"paged={previousPage + 1}/{pages}({previousFraction:F3}); actualScroll={actualScroll:F3}"));
            reader.TestNovelScrollViewportToFraction(0.70);
            bool moved = await WaitLogicAuditAsync(() => Math.Abs(reader.NovelActualScrollFraction - 0.70) < 0.02);
            reader.NovelModePaged.IsChecked = true;
            bool pagedReady = await WaitLogicAuditAsync(() => reader.IsNovelChapterLayoutStable
                && reader.NovelDisplayedPageNumber == reader.NovelPage + 1
                && Math.Abs((double)reader.NovelPage / Math.Max(1, reader.NovelDisplayedPageCount - 1) - 0.70)
                    <= Math.Max(0.12, 1.0 / Math.Max(1, reader.NovelDisplayedPageCount - 1) + 0.04));
            double newFraction = (double)reader.NovelPage / Math.Max(1, reader.NovelDisplayedPageCount - 1);
            double tolerance = Math.Max(0.12, 1.0 / Math.Max(1, reader.NovelDisplayedPageCount - 1) + 0.04);
            checks.Add(("小说滚动70百分比切翻页不用旧游标", moved && pagedReady && reader.NovelPage != previousPage
                && Math.Abs(newFraction - 0.70) <= tolerance
                && reader.NovelDisplayedPageNumber == reader.NovelPage + 1,
                $"oldPage={previousPage + 1}; newPage={reader.NovelDisplayedPageNumber}/{reader.NovelDisplayedPageCount}; fraction={newFraction:F3}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-novel-mode-position.png"));
        }
        catch (Exception ex) { checks.Add(("小说模式保位异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task CheckAutoScrollInputLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditNovelAsync(outDir, "QA-未滚轮先自动滚动");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitNovelLogicAuditAsync(reader);
            reader.NovelModeScroll.IsChecked = true;
            await WaitNovelLogicAuditAsync(reader);
            reader.TestNovelScrollViewportToFraction(0.15); // 不触发 wheel，避免预先安装滚轮输入监听掩盖缺陷。
            await WaitLogicAuditAsync(() => Math.Abs(reader.NovelActualScrollFraction - 0.15) < 0.02);
            var scroll = VisualTreeHelpers.FindVisualChild<ScrollViewer>(reader.NovelScroll)
                ?? throw new InvalidOperationException("正文 ScrollViewer 缺失");
            double start = scroll.VerticalOffset;
            reader.AutoScrollToggle.IsChecked = true;
            bool started = await WaitLogicAuditAsync(() => scroll.VerticalOffset > start + 12);
            scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Task.Delay(100);
            double stopped = scroll.VerticalOffset;
            bool toggledOff = reader.AutoScrollToggle.IsChecked != true;
            await Task.Delay(400);
            checks.Add(("未滚轮直接自动滚动后正文点击停止", started && toggledOff
                && Math.Abs(scroll.VerticalOffset - stopped) < 1,
                $"started={started}; toggleOff={toggledOff}; start={start:F1}; atClick={stopped:F1}; after400ms={scroll.VerticalOffset:F1}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-auto-scroll-stop.png"));
        }
        catch (Exception ex) { checks.Add(("自动滚动输入异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static IEnumerable<DependencyObject> LogicAuditVisuals(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in LogicAuditVisuals(child)) yield return descendant;
        }
    }

    private static void ShowLogicAuditBookmarks(ReaderWindow reader)
    {
        if (reader.BookmarkPanel.Visibility != Visibility.Visible)
            reader.BookmarkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        reader.UpdateLayout();
    }

    private static void AddLogicAuditBookmark(ReaderWindow reader)
    {
        ShowLogicAuditBookmarks(reader);
        var add = LogicAuditVisuals(reader.BookmarkPanel).OfType<Button>()
            .Single(b => b.Content is string text && text.Contains("添加当前页书签"));
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task CheckBookmarkDeleteLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditNovelAsync(outDir, "QA-书签删除不导航");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitNovelLogicAuditAsync(reader);
            reader.TestNovelGoToChapter(1);
            await WaitNovelLogicAuditAsync(reader);
            AddLogicAuditBookmark(reader);
            long firstId = App.Library.GetBookmarks(book.Id).Single().Id;
            reader.TestNovelGoToChapter(2);
            await WaitNovelLogicAuditAsync(reader);
            AddLogicAuditBookmark(reader);
            long secondId = App.Library.GetBookmarks(book.Id).Single(b => b.Id != firstId).Id;
            reader.TestNovelGoToChapter(0);
            await WaitNovelLogicAuditAsync(reader);
            ShowLogicAuditBookmarks(reader);
            var selected = reader.BookmarkList.Items.Cast<BookmarkItem>().Single(b => b.Bookmark.Id == firstId);
            reader.BookmarkList.SelectedItem = selected;
            reader.BookmarkList.ScrollIntoView(selected);
            reader.BookmarkList.UpdateLayout();
            var row = reader.BookmarkList.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem
                ?? throw new InvalidOperationException("书签行未生成");
            var delete = LogicAuditVisuals(row).OfType<Button>().Single(b => b.Tag is BookmarkItem);
            int beforeChapter = reader.NovelChapterIndex, beforePage = reader.NovelDisplayedPageNumber;
            // 通用 PreviewMouseUp 在隧道路由各级重新派生左键事件，必须经过真实 ListBox 父处理器。
            var deleteHit = LogicAuditVisuals(delete).OfType<FrameworkElement>()
                .FirstOrDefault(e => e is TextBlock or System.Windows.Shapes.Path) ?? delete;
            deleteHit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            await WaitNovelLogicAuditAsync(reader);
            checks.Add(("书签Delete预览路由不先导航或隐藏", reader.NovelChapterIndex == beforeChapter
                && reader.NovelDisplayedPageNumber == beforePage && reader.BookmarkPanel.Visibility == Visibility.Visible
                && App.Library.GetBookmarks(book.Id).Count == 2,
                $"before={beforeChapter}/{beforePage}; after={reader.NovelChapterIndex}/{reader.NovelDisplayedPageNumber}; panel={reader.BookmarkPanel.Visibility}"));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterDelete = App.Library.GetBookmarks(book.Id);
            checks.Add(("书签Delete只删除目标", afterDelete.Count == 1 && afterDelete[0].Id == secondId
                && reader.NovelChapterIndex == beforeChapter && reader.NovelDisplayedPageNumber == beforePage,
                $"remaining={string.Join(",", afterDelete.Select(b => b.Id))}; deleted={firstId}; kept={secondId}"));
            ShowLogicAuditBookmarks(reader);
            reader.BookmarkList.SelectedItem = reader.BookmarkList.Items.Cast<BookmarkItem>().Single();
            reader.BookmarkList.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            await WaitNovelLogicAuditAsync(reader);
            checks.Add(("书签列表空白不激活已选行", reader.NovelChapterIndex == beforeChapter
                && reader.NovelDisplayedPageNumber == beforePage && reader.BookmarkPanel.Visibility == Visibility.Visible,
                $"chapter={reader.NovelChapterIndex}; page={reader.NovelDisplayedPageNumber}; panel={reader.BookmarkPanel.Visibility}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-bookmark-delete.png"));
        }
        catch (Exception ex) { checks.Add(("书签事件路由异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static bool RaiseLogicAuditKey(ReaderWindow reader, Key key)
    {
        reader.Focus();
        var source = PresentationSource.FromVisual(reader);
        if (source is null) return false;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        reader.RaiseEvent(args);
        return args.Handled;
    }

    private static async Task CheckNovelEndIntentLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await ImportLogicAuditNovelAsync(outDir, "QA-章末意图不残留");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitNovelLogicAuditAsync(reader);
            reader.NovelModeScroll.IsChecked = true;
            await WaitNovelLogicAuditAsync(reader);
            bool endHandled = RaiseLogicAuditKey(reader, Key.End);
            bool reachedEnd = await WaitLogicAuditAsync(() => reader.IsNovelChapterLayoutStable
                && reader.NovelChapterIndex == 2 && reader.NovelActualScrollFraction > 0.95);
            checks.Add(("滚动End确实到最后章末", endHandled && reachedEnd,
                $"handled={endHandled}; chapter={reader.NovelChapterIndex}; fraction={reader.NovelActualScrollFraction:F3}"));
            reader.NovelModePaged.IsChecked = true;
            await WaitNovelLogicAuditAsync(reader);
            reader.TestNovelGoToChapter(0);
            await WaitNovelLogicAuditAsync(reader);
            reader.TestNovelGoToChapter(2);
            bool stable = await WaitNovelLogicAuditAsync(reader);
            checks.Add(("滚动End后换模式再进末章不继承章末意图", stable && reader.NovelChapterIndex == 2
                && reader.NovelPage == 0 && reader.NovelDisplayedPageNumber == 1,
                $"model={reader.NovelPage}; displayed={reader.NovelDisplayedPageNumber}/{reader.NovelDisplayedPageCount}"));
            // 强制建立最后章首页，不让上一条失败污染“上一章末页 intent 被中断”这一独立路径。
            reader.TestSliderDrag(0, 2.0 / reader.NovelChapterCount);
            await WaitLogicAuditAsync(() => reader.IsNovelChapterLayoutStable
                && reader.NovelChapterIndex == 2 && reader.NovelDisplayedPageNumber == 1);
            reader.PrevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.TestNovelGoToChapter(0); // 同一UI回合打断 pending 上一章末页定位。
            await WaitNovelLogicAuditAsync(reader);
            reader.TestNovelGoToChapter(1);
            stable = await WaitNovelLogicAuditAsync(reader);
            checks.Add(("上一章末页意图中断后不残留", stable && reader.NovelChapterIndex == 1
                && reader.NovelPage == 0 && reader.NovelDisplayedPageNumber == 1,
                $"chapter={reader.NovelChapterIndex}; model={reader.NovelPage}; displayed={reader.NovelDisplayedPageNumber}/{reader.NovelDisplayedPageCount}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-end-intent.png"));
        }
        catch (Exception ex) { checks.Add(("章末意图测试异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task CheckEarlyReaderStateLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var comic = App.Library.GetBookByPath(Path.Combine(outDir, "QA-首次条漫模式竞态.cbz"))
                ?? throw new InvalidOperationException("早关守卫缺少漫画fixture");
            App.Library.SaveProgress(comic.Id, 0.75, 8, 0.75, 0, "webtoon", 1.2);
            comic = App.Library.GetBook(comic.Id)!;
            reader = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader.Show();
            reader.Close(); // 新守卫回归：不等待Loaded/初始化，也不声称旧版已实测。
            reader = null;
            await Task.Delay(150);
            var earlyComic = App.Library.GetProgress(comic.Id);
            checks.Add(("漫画Show即Close新守卫不清零历史进度", earlyComic is { PageIndex: 8, Mode: "webtoon" }
                && Math.Abs(earlyComic.ScrollOffset - 0.75) < 0.0001 && Math.Abs(earlyComic.Zoom - 1.2) < 0.0001
                && Math.Abs((App.Library.GetBook(comic.Id)?.Progress ?? -1) - 0.75) < 0.0001,
                $"guardOnly; DB={earlyComic?.Mode}/{earlyComic?.PageIndex}/{earlyComic?.ScrollOffset:F4}/{earlyComic?.Zoom:F2}"));

            var novel = await ImportLogicAuditNovelAsync(outDir, "QA-TXT打开即关守卫");
            App.Library.SaveProgress(novel.Id, 0.55, 3, 0.61, 1, "novel:scroll", 0);
            novel = App.Library.GetBook(novel.Id)!;
            reader = new ReaderWindow(novel) { ShowInTaskbar = false };
            reader.Show();
            reader.Close();
            reader = null;
            await Task.Delay(150);
            var earlyNovel = App.Library.GetProgress(novel.Id);
            checks.Add(("TXTShow即Close新守卫不清零历史进度", earlyNovel is { PageIndex: 3, ChapterIndex: 1, Mode: "novel:scroll" }
                && Math.Abs(earlyNovel.ScrollOffset - 0.61) < 0.0001
                && Math.Abs((App.Library.GetBook(novel.Id)?.Progress ?? -1) - 0.55) < 0.0001,
                $"guardOnly; DB={earlyNovel?.Mode}/{earlyNovel?.ChapterIndex}/{earlyNovel?.PageIndex}/{earlyNovel?.ScrollOffset:F4}"));

            // 真实Checked路由在首次Webtoon初始化仍pending时切走，不靠私有状态或外部宿主。
            App.Library.SaveProgress(comic.Id, 0.75, 8, 0.75, 0, "webtoon", 1);
            var openingReader = new ReaderWindow(App.Library.GetBook(comic.Id)!) { ShowInTaskbar = false };
            reader = openingReader;
            bool interrupted = false;
            openingReader.ModeWebtoon.Checked += (_, _) =>
            {
                if (interrupted || openingReader.IsWebtoonReady) return;
                interrupted = true;
                openingReader.TestSwitchToPaged();
            };
            openingReader.Show();
            bool keptPage = await WaitLogicAuditAsync(() => openingReader.IsComicPageLoaded
                && openingReader.ModePaged.IsChecked == true && openingReader.CurrentPageNumber == 8
                && LogicAuditPagePixelsMatch(openingReader.PageImage, 8));
            openingReader.TestFlushSave();
            var interruptedSaved = App.Library.GetProgress(comic.Id);
            checks.Add(("已存条漫未ready切模式不采默认负页", interrupted && keptPage
                && interruptedSaved is { Mode: "paged", PageIndex: 8 },
                $"guardOnly; interrupted={interrupted}; actual={openingReader.CurrentPageNumber}; DB={interruptedSaved?.Mode}/{interruptedSaved?.PageIndex}"));
            Capture(openingReader, Path.Combine(outDir, "qa-logic-saved-webtoon-interrupted.png"));
        }
        catch (Exception ex) { checks.Add(("早关与初始位置守卫异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static double? ReadLogicAuditSavedMargin(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar() as string;
        if (value is null) return null;
        using var json = JsonDocument.Parse(value);
        return json.RootElement.GetProperty(nameof(NovelViewSettings.PageMargin)).GetDouble();
    }

    private static async Task CheckReaderSettingsRetryLogicAuditAsync(string outDir,
        List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        SqliteConnection? connection = null;
        try
        {
            var book = await ImportLogicAuditNovelAsync(outDir, "QA-设置失败后关闭重试");
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitNovelLogicAuditAsync(reader);
            reader.TestSetNovelSpacingText(false, "37");
            await Task.Delay(600);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = App.Library.DbPath }.ToString());
            connection.Open();
            string key = "novel_settings_book_" + book.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            double? baseline = ReadLogicAuditSavedMargin(connection, key);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TRIGGER qa_logic_settings_insert BEFORE INSERT ON settings WHEN NEW.key='{key}'
                    BEGIN SELECT RAISE(FAIL,'QA requested settings failure'); END;
                    CREATE TRIGGER qa_logic_settings_update BEFORE UPDATE ON settings WHEN NEW.key='{key}'
                    BEGIN SELECT RAISE(FAIL,'QA requested settings failure'); END;
                    """;
                command.ExecuteNonQuery();
            }
            reader.TestSetNovelSpacingText(false, "113");
            await Task.Delay(600);
            double? duringFailure = ReadLogicAuditSavedMargin(connection, key);
            checks.Add(("设置写失败注入确实隔离DB与会话", baseline == 37 && duringFailure == 37
                && reader.NovelFormattingSnapshot.PageMargin == 113,
                $"beforeDB={baseline}; failedDB={duringFailure}; session={reader.NovelFormattingSnapshot.PageMargin}"));
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER qa_logic_settings_insert; DROP TRIGGER qa_logic_settings_update;";
                command.ExecuteNonQuery();
            }
            reader.TestSetNovelSpacingText(false, "113"); // 同值，不制造另一条改变值的 dirty 信号。
            reader.Close();
            reader = null;
            double? afterClose = ReadLogicAuditSavedMargin(connection, key);
            App.Settings = new AppSettingsService(App.Library);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool restored = await WaitNovelLogicAuditAsync(reader);
            checks.Add(("写失败恢复后同值关闭真正持久化", afterClose == 113 && restored
                && reader.NovelFormattingSnapshot.PageMargin == 113 && reader.NovelDocumentMargin == 113,
                $"afterCloseDB={afterClose}; reopened={reader.NovelFormattingSnapshot.PageMargin}; document={reader.NovelDocumentMargin}"));
            Capture(reader, Path.Combine(outDir, "qa-logic-settings-retry.png"));
        }
        catch (Exception ex) { checks.Add(("设置失败注入测试异常", false, ex.ToString())); }
        finally
        {
            if (connection?.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "DROP TRIGGER IF EXISTS qa_logic_settings_insert; DROP TRIGGER IF EXISTS qa_logic_settings_update;";
                    command.ExecuteNonQuery();
                }
                catch (Exception ex) { checks.Add(("设置失败注入清理异常", false, ex.ToString())); }
            }
            reader?.Close();
            connection?.Dispose();
        }
    }
}
