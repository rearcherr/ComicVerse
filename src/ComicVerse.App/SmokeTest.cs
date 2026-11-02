using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComicVerse.App.Controls;
using ComicVerse.App.Helpers;
using ComicVerse.App.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Models;

namespace ComicVerse.App;

/// <summary>无人工参与的启动冒烟测试：导入样例 → 打开漫画/小说阅读器 → 截图并断言。</summary>
public static partial class SmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string samplesDir = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a))
                            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));
        string outDir = Environment.GetEnvironmentVariable("SMOKE_OUT_DIR") ?? Path.Combine(Path.GetTempPath(), "comicverse-smoke");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "smoke-environment.json"),
            System.Text.Json.JsonSerializer.Serialize(App.GetSmokeProcessEnvironment()));
        if (args.Contains("--verify-task-failure-exit"))
        {
            CreateUnobservedTaskForSafetyCheck();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(1000);
            return 2;
        }
        if (args.Contains("--verify-failure-exit"))
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                throw new InvalidOperationException("QA: intentional dispatcher failure to verify noninteractive exit")));
            await Task.Delay(5000);
            return 2; // Reaching this line means the fatal dispatcher error failed to stop the test.
        }

        // 冒烟在自己进程里跑：任何未被捕获的异常（包括 DispatcherTimer 回调里的）都要落到磁盘，
        // 否则进程会静默退出、summary 完全不产出，排查时没有任何线索。
        void DumpCrash(string tag, Exception? ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(outDir, "smoke-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {tag}: {ex}\n\n", System.Text.Encoding.UTF8);
            }
            catch { }
        }
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += (_, e) => { DumpCrash("DispatcherUnhandledException", e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { DumpCrash("AppDomainUnhandled", e.ExceptionObject as Exception); };

        var main = new MainWindow();
        main.Show();
        await Task.Delay(900);
        main.Refresh();
        bool shelfEmptyOk = main.ShelfCount == 0 && main.EmptyHeadingText == "书架还是空的";
        if (args.Contains("--logic-audit-only"))
        {
            var audit = await CheckLogicAuditAsync(main, outDir);
            string auditSummary = $"整体逻辑={audit.Ok} — {audit.Detail}";
            File.WriteAllText(Path.Combine(outDir, "logic-audit-summary.txt"), auditSummary);
            Console.WriteLine(auditSummary);
            main.Close();
            return audit.Ok ? 0 : 1;
        }
        if (args.Contains("--novel-expansion-only"))
        {
            var expansion = await CheckNovelExpansionAsync(outDir);
            string expansionSummary = $"排版扩展={expansion.Spacing} 正文查找={expansion.Search} — {expansion.Detail}";
            File.WriteAllText(Path.Combine(outDir, "novel-expansion-summary.txt"), expansionSummary);
            Console.WriteLine(expansionSummary);
            main.Close();
            return expansion.Spacing && expansion.Search ? 0 : 1;
        }

        var result = await App.Importer.ImportAsync(new[] { samplesDir });
        if (args.Contains("--webtoon-input-only"))
        {
            var inputBook = App.Library.GetBooks(filter: "comic").First(b => b.Format == BookFormat.Cbz);
            var inputReader = new ReaderWindow(inputBook) { ShowInTaskbar = false };
            inputReader.Show();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!inputReader.IsWebtoonReady && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
            var check = inputReader.TestWebtoonClickRelease();
            string inputSummary = $"条漫点击释放: {check.Ok} | {check.Detail}";
            File.WriteAllText(Path.Combine(outDir, "input-summary.txt"), inputSummary);
            Console.WriteLine(inputSummary);
            inputReader.Close();
            main.Close();
            return check.Ok ? 0 : 1;
        }
        // 现场生成一本 30 页长漫画，用于真实覆盖“进度条远距离跳页”
        string longCbz = CreateLongComic(outDir);
        var longResult = await App.Importer.ImportAsync(new[] { longCbz });
        result.Imported += longResult.Imported;
        result.Updated += longResult.Updated;
        result.Failed.AddRange(longResult.Failed);
        main.Refresh();
        var shelfChecks = await CheckShelfImprovementsAsync(main, outDir);
        bool unequalWebtoonOk = await CheckUnequalWebtoonSwitchAsync(outDir);
        var comic = App.Library.GetBooks(filter: "comic").OrderByDescending(b => b.PageCount).FirstOrDefault();
        var novel = App.Library.GetBooks(filter: "novel").FirstOrDefault();

        // 导入新文件后书架必须立即出现（此前列表视图要重启才显示）
        bool shelfRefreshOk = false;
        try
        {
            int beforeCount = main.ShelfCount;
            string freshCbz = CreateLongComic(outDir, 3, "fresh-import.cbz");
            await main.TestImportAsync(new[] { freshCbz });
            shelfRefreshOk = main.ShelfContains("fresh-import") && main.ShelfCount > beforeCount;
        }
        catch
        {
            shelfRefreshOk = false;
        }

        bool comicReaderOk = false;
        bool webtoonOk = false;
        bool doubleOk = false;
        bool novelReaderOk = false;
        bool novelActualPagingOk = false;
        string novelActualPagingDetail = "";
        bool novelScrollAutosaveOk = false;
        bool shortNovelCompleteOk = false;
        bool pdfReaderOk = false;
        bool pagingOk = false;
        bool webtoonScrollOk = false;
        bool farJumpOk = false;
        bool webtoonFarJumpOk = false;
        bool webtoonDragOk = false;
        bool webtoonClickReleaseOk = false;
        string webtoonClickDetail = "";
        bool webtoonHorizontalPanOk = false;
        bool webtoonProgressOk = false;
        bool webtoonModeReentryOk = false;
        double webtoonHorizontalScrollableWidth = -1;
        int webtoonSeamRows = -1;
        bool webtoonArrowOk = false;
        string webtoonArrowDetail = "";
        bool sliderDragOk = false;
        int sliderDragBlank = -1;
        int sliderDragPage = -1;
        double sliderDragMs = -1;
        double sliderDragInk = -1;
        double sliderDragOffset = -1;
        string sliderDragRange = "";
        int farJumpPage = -1;
        bool defaultModeOk = false;
        bool defaultOpensWebtoon = false;
        bool modePersisted = false;
        bool zoomPersisted = false;
        bool zoomTextSyncOk = false;
        bool zoomTextRestored = false;
        bool novelProgressOk = false;
        bool novelScrollChapterOk = false;
        bool novelLightThemeOk = false;
        bool novelRestoreOk = false;
        bool novelArrowOk = false;
        string novelArrowDetail = "";
        bool doubleToPagedOk = false;
        string doubleToPagedDetail = "";
        bool doubleZoomOk = false;
        bool doubleRtlOrderOk = false;
        bool doubleZoomRestored = false;
        double doubleZoomSaved = -1;
        string doubleRtlDetail = "";
        bool novelScrollRestored = false;
        string novelScrollRestoreDetail = "";
        bool novelScrollSkipped = false;
        bool longNovelReaderOk = false;
        bool longNovelGenOk = false;
        bool longNovelScrollRestoredOk = false;
        int longNovelChapterBefore = -1;
        int longNovelChapterAfter = -1;
        double longNovelScrollFractionBefore = -1;
        double longNovelScrollFractionAfter = -1;
        double longNovelActualScrollFractionAfter = -1;
        double longNovelScrollableHeight = -1;
        string longNovelSubModeAfter = "";
        string longNovelOpenProbe = "";
        string longNovelScrollProbe = "";
        string longNovelReopenProbe = "";
        string longNovelSaveProbe = "";
        string longNovelScrollRestoreDetail = "";
        string novelRestoreDetail = "";
        bool novelFontSizeKept = false;
        bool novelFontSizeLeaked = false;
        double closeSeconds = -1;
        bool tallWebtoonOk = false;
        double tallWebtoonSeconds = -1;
        string? comicError = null;
        string readerWebtoonStats = "";
        int readerWebtoonPages = 0;
        double openStallMs = -1;
        double sessionStallMs = -1;
        double longPdfOpenSeconds = -1;
        double longPdfStallMs = -1;

        var stall = new StallProbe();

        if (comic is not null)
        {
            stall.Start();
            var reader = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1600);
            reader.TestSwitchToPaged();
            await Task.Delay(700);
            openStallMs = stall.MaxStallMs;
            stall.Reset();
            comicReaderOk = reader.IsComicPageLoaded;
            Capture(reader, Path.Combine(outDir, "reader-paged.png"));
            // 快速翻页压力测试
            pagingOk = true;
            for (int i = 0; i < 4; i++)
            {
                reader.TestNext();
                await Task.Delay(120);
            }
            await Task.Delay(600);
            pagingOk = reader.IsComicPageLoaded;
            // 远距离跳页：连续快速跳到靠后页，末页不应白屏
            int farLast = reader.ComicPageCount - 1;
            reader.TestJumpToPage(farLast);
            await Task.Delay(100);
            reader.TestJumpToPage(Math.Max(0, farLast - 2));
            await Task.Delay(100);
            reader.TestJumpToPage(farLast);
            await Task.Delay(1000);
            farJumpPage = reader.CurrentPageNumber;
            farJumpOk = reader.IsComicPageLoaded && farJumpPage == farLast;
            Capture(reader, Path.Combine(outDir, "reader-far-jump.png"));
            try
            {
                reader.TestSwitchToWebtoon();
                await Task.Delay(1600);
                webtoonOk = reader.IsWebtoonReady;
                readerWebtoonStats = reader.WebtoonStats;
                Capture(reader, Path.Combine(outDir, "reader-webtoon.png"));
                reader.TestWebtoonScrollBy(1600);
                await Task.Delay(900);
                webtoonScrollOk = reader.IsWebtoonReady && reader.WebtoonRenderedCount > 0;
                // 放大条漫后必须能横向查看被裁掉的左右边缘
                reader.TestSetZoom(2.0);
                await Task.Delay(700);
                webtoonHorizontalScrollableWidth = reader.WebtoonHorizontalScrollableWidth;
                reader.TestScrollWebtoonHorizontal(100);
                await Task.Delay(150);
                webtoonHorizontalPanOk = webtoonHorizontalScrollableWidth > 10 && reader.WebtoonHorizontalOffset > 1;
                reader.TestSetZoom(1.0);
                await Task.Delay(500);
                // 模拟拖动进度条：连续快速跳到靠后页，再落到末页
                int last = reader.WebtoonPageCount - 1;
                for (int i = 4; i >= 0; i--)
                {
                    reader.TestWebtoonJumpTo(Math.Max(0, last - i));
                    await Task.Delay(50);
                }
                await Task.Delay(1500);
                webtoonFarJumpOk = reader.IsWebtoonReady && reader.WebtoonRenderedCount > 0
                    && reader.WebtoonRenderedLoadedCount > 0 && reader.CurrentPageNumber == last;
                webtoonSeamRows = BackgroundRowCount(reader); // 相邻页之间不应漏出背景（细白线）
                // 左右方向键：条漫是纵向滚动，始终 ← 上一页（上）/ → 下一页（下），不受日漫 RTL 设置影响
                int arrowMid = Math.Max(0, last / 2);
                App.Settings.MangaRightToLeft = true; // 复现用户环境（其库中 manga_rtl=1）
                reader.TestSetRtl(true);
                reader.TestWebtoonJumpTo(arrowMid);
                await Task.Delay(400);
                int arrowStartPage = reader.WebtoonCurrentPage;
                double arrowStartOffset = reader.WebtoonScrollOffset;
                reader.TestPressArrowKey(isLeftKey: false); // →
                await Task.Delay(450);
                int arrowRightPage = reader.WebtoonCurrentPage;
                double arrowRightOffset = reader.WebtoonScrollOffset;
                reader.TestPressArrowKey(isLeftKey: true); // ←
                await Task.Delay(450);
                int arrowLeftPage = reader.WebtoonCurrentPage;
                double arrowLeftOffset = reader.WebtoonScrollOffset;
                webtoonArrowOk = arrowRightPage > arrowStartPage && arrowRightOffset > arrowStartOffset
                                 && arrowLeftPage < arrowRightPage && arrowLeftOffset < arrowRightOffset;
                webtoonArrowDetail =
                    $"RTL=1 第 {arrowStartPage + 1} 页(偏移 {arrowStartOffset:F0}) → 右键到第 {arrowRightPage + 1} 页({arrowRightOffset:F0}) → 左键回第 {arrowLeftPage + 1} 页({arrowLeftOffset:F0})";
                App.Settings.MangaRightToLeft = false;
                reader.TestSetRtl(false);
                reader.TestFlushSave();
                double webtoonProgress = App.Library.GetBook(comic.Id)?.Progress ?? -1;
                webtoonProgressOk = webtoonProgress is > 0.2 and < 0.85;

                int reentryPage = reader.CurrentPageNumber;
                reader.TestSwitchToPaged();
                await Task.Delay(500);
                reader.TestSwitchToWebtoon();
                await Task.Delay(1000);
                webtoonModeReentryOk = Math.Abs(reader.CurrentPageNumber - reentryPage) <= 1;
                var clickCheck = reader.TestWebtoonClickRelease();
                webtoonClickReleaseOk = clickCheck.Ok;
                webtoonClickDetail = clickCheck.Detail;
                // 鼠标左键拖拽滑动：向下拖 180px，滚动偏移应同步上移约 180px
                double beforeDrag = reader.WebtoonScrollOffset;
                reader.TestWebtoonDragDown(180);
                await Task.Delay(400); // 滚动偏移是延迟生效的，等它落地再比较
                double afterDrag = reader.WebtoonScrollOffset;
                await Task.Delay(500); // 松手后应完全静止：没有惯性漂移
                double afterIdle = reader.WebtoonScrollOffset;
                webtoonDragOk = Math.Abs(afterDrag - beforeDrag + 180) < 60
                                && Math.Abs(afterIdle - afterDrag) < 2;
                Capture(reader, Path.Combine(outDir, "reader-webtoon-far-jump.png"));
                // 大幅拖动进度条：松手后视口内的每一屏都必须真正加载出图片（不能空白）
                reader.TestSliderDrag(0.05, 0.92);
                var dragSw = System.Diagnostics.Stopwatch.StartNew();
                double dragInk = 0;
                while (dragSw.Elapsed < TimeSpan.FromSeconds(8))
                {
                    dragInk = ContentInkRatio(reader);
                    if (reader.WebtoonRenderedCount > 0 && reader.WebtoonBlankStripCount == 0 && dragInk > 0.05)
                        break;
                    await Task.Delay(200);
                }
                sliderDragMs = Math.Round(dragSw.Elapsed.TotalMilliseconds);
                sliderDragPage = reader.CurrentPageNumber;
                sliderDragInk = dragInk;
                sliderDragOffset = reader.WebtoonScrollOffset;
                var range = reader.WebtoonVisibleRange;
                sliderDragRange = $"{range.First + 1}-{range.Last + 1}";
                sliderDragBlank = reader.WebtoonBlankStripCount;
                sliderDragOk = reader.IsWebtoonReady && reader.WebtoonRenderedCount > 0
                    && sliderDragBlank == 0 && sliderDragInk > 0.05;
                Capture(reader, Path.Combine(outDir, "reader-webtoon-slider-drag.png"));
                reader.TestSwitchToDouble();
                await Task.Delay(1400);
                doubleOk = reader.IsDoubleReady;
                Capture(reader, Path.Combine(outDir, "reader-double.png"));
                double beforeDoubleZoom = reader.DoubleAppliedScale;
                reader.TestZoomIn();
                await Task.Delay(300);
                doubleZoomOk = reader.DoubleAppliedScale > beforeDoubleZoom + 0.01;

                // RTL 双页应把后一页放到左侧、起始页放到右侧
                int rtlsStart = reader.CurrentSpreadStart;
                reader.TestSetRtl(true);
                reader.TestRefreshDouble();
                await Task.Delay(900);
                doubleRtlOrderOk = reader.DoubleRightPageIndex == rtlsStart
                                   && reader.DoubleLeftPageIndex == rtlsStart + 1;
                doubleRtlDetail = $"起始页 {rtlsStart + 1}，左 {reader.DoubleLeftPageIndex + 1}，右 {reader.DoubleRightPageIndex + 1}";
                reader.TestSetRtl(false);
                reader.TestRefreshDouble();
                await Task.Delay(900);

                // 双页 → 翻页：游标必须跟着走，不能跳回进双页之前的旧页（旧的 _page）
                int spreadBeforeSwitch = reader.CurrentSpreadStart;
                for (int i = 0; i < 3; i++)
                {
                    reader.TestNext();
                    await Task.Delay(140);
                }
                int spreadAfterForward = reader.CurrentSpreadStart;
                reader.TestSwitchToPaged();
                await Task.Delay(800);
                int pageAfterBack = reader.CurrentPageNumber;
                doubleToPagedOk = reader.IsComicPageLoaded && pageAfterBack >= spreadAfterForward;
                doubleToPagedDetail =
                    $"双页起点 {spreadBeforeSwitch + 1} → 前进到 {spreadAfterForward + 1} → 切回翻页落在第 {pageAfterBack + 1} 页";
                Capture(reader, Path.Combine(outDir, "reader-double-to-paged.png"));
                reader.TestSwitchToDouble();
                await Task.Delay(900);
                reader.TestSetZoom(1.4);
                await Task.Delay(400);
                reader.TestFlushSave();
                doubleZoomSaved = reader.DoubleAppliedScale;
            }
            catch (Exception ex)
            {
                comicError = ex.Message;
            }
            var closeSw = System.Diagnostics.Stopwatch.StartNew();
            reader.Close();
            closeSw.Stop();
            closeSeconds = closeSw.Elapsed.TotalSeconds;

            var doubleReader = new ReaderWindow(comic) { ShowInTaskbar = false };
            doubleReader.Show();
            await Task.Delay(1600);
            doubleZoomRestored = doubleReader.IsDoubleReady && Math.Abs(doubleReader.CurrentZoom - doubleZoomSaved) < 0.05;
            doubleReader.Close();

            // 默认阅读方式应为条漫：新建阅读器直接进入条漫
            defaultModeOk = App.Settings.DefaultComicMode == "webtoon";
            // 清空该书的模式记忆，验证“无记忆时”默认进入条漫
            App.Library.SaveProgress(comic.Id, comic.Progress, 0, 0, 0, "", 0);
            var reader2 = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader2.Show();
            await Task.Delay(2200);
            defaultOpensWebtoon = reader2.IsWebtoonReady;
            Capture(reader2, Path.Combine(outDir, "default-webtoon.png"));

            // 缩放同步与更小缩放下限（20%）
            reader2.TestSetZoom(0.3);
            await Task.Delay(400);
            zoomTextSyncOk = reader2.ZoomTextValue == "30%" && Math.Abs(reader2.CurrentZoom - 0.3) < 0.02;

            // 阅读方式与缩放比例按书记忆
            reader2.TestSwitchToPaged();
            reader2.TestSetZoom(1.5);
            await Task.Delay(800);
            reader2.Close();

            var reader3 = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader3.Show();
            await Task.Delay(1800);
            modePersisted = reader3.IsComicPageLoaded;
            zoomPersisted = Math.Abs(reader3.CurrentZoom - 1.5) < 0.05;
            zoomTextRestored = reader3.ZoomTextValue == "150%";
            Capture(reader3, Path.Combine(outDir, "mode-zoom-restored.png"));
            reader3.Close();
            sessionStallMs = stall.MaxStallMs;
            stall.Stop();
        }

        if (novel is not null)
        {
            var reader = new ReaderWindow(novel) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1800);
            novelReaderOk = reader.IsNovelReady;
            // 注意：TestNovelNextPage 走的是翻页子模式，此时 _chapterPages 可能还没算完；
            // 这里只验证「阅读器可用 + 能前进」，不再依赖"换章后页码被重置为 0"这种副作用。
            reader.TestNovelNextPage();
            reader.TestNovelNextPage();
            int pageBeforeChapterJump = reader.NovelPage;
            reader.TestNovelNextChapter();
            await Task.Delay(700);
            novelProgressOk = pageBeforeChapterJump >= 0;
            // 滚动模式：滚到章末继续下滑应自动进入下一章
            reader.NovelModeScroll.IsChecked = true;
            await Task.Delay(600);
            reader.TestNovelGoToChapter(0);          // 回到第 1 章，确保后面还有下一章
            await Task.Delay(600);
            int chapterBeforeScroll = reader.NovelChapterIndex;
            reader.TestNovelScrollToEdge(true);
            await Task.Delay(700);
            bool stepped = reader.TestNovelScrollStep(1);
            await Task.Delay(600);
            novelScrollChapterOk = stepped && reader.NovelChapterIndex == chapterBeforeScroll + 1;
            reader.NovelModePaged.IsChecked = true;
            await Task.Delay(400);
            int novelChapterBefore = reader.NovelChapterIndex;
            novelProgressOk = novelChapterBefore >= 1;
            // 方向键在小说里同样不应受日漫 RTL 影响：← 恒为上一章/上一页，→ 恒为下一章/下一页
            App.Settings.MangaRightToLeft = true;
            reader.TestSetRtl(true);
            reader.TestNovelGoToChapter(0);
            await Task.Delay(700);
            int novelArrowBase = reader.NovelChapterIndex * 1000 + reader.NovelPage;
            reader.TestPressArrowKey(isLeftKey: false); // →
            await Task.Delay(500);
            int novelArrowRight = reader.NovelChapterIndex * 1000 + reader.NovelPage;
            reader.TestPressArrowKey(isLeftKey: true);  // ←
            await Task.Delay(500);
            int novelArrowLeft = reader.NovelChapterIndex * 1000 + reader.NovelPage;
            novelArrowOk = novelArrowRight > novelArrowBase && novelArrowLeft < novelArrowRight;
            novelArrowDetail =
                $"RTL=1 第 {novelArrowBase / 1000 + 1} 章(页位 {novelArrowBase % 1000}) → 右键到第 {novelArrowRight / 1000 + 1} 章(页位 {novelArrowRight % 1000}) → 左键回第 {novelArrowLeft / 1000 + 1} 章(页位 {novelArrowLeft % 1000})";
            App.Settings.MangaRightToLeft = false;
            reader.TestSetRtl(false);
            reader.TestNovelGoToChapter(novelChapterBefore);
            await Task.Delay(600);
            reader.TestToggleTheme();
            await Task.Delay(900);
            novelLightThemeOk = reader.NovelBackgroundHex.Length > 0 && !reader.NovelBackgroundHex.Contains("1A1A2E");
            reader.TestToggleTheme();
            await Task.Delay(600);
            Capture(reader, Path.Combine(outDir, "novel-paged.png"));

            // 排版面板改字号：必须被记住，且不能改动"全局默认"（以前每个窗口都会回写 App.Settings）。
            int globalFontBefore = (int)App.Settings.NovelSettings.FontSize;
            reader.TestSetFontSize(30);
            await Task.Delay(900);
            reader.TestToggleTheme();
            await Task.Delay(500);
            reader.TestToggleTheme();
            await Task.Delay(500);
            novelFontSizeKept = Math.Abs(reader.FontSizeSetting - 30) < 0.5;
            novelFontSizeLeaked = Math.Abs(App.Settings.NovelSettings.FontSize - globalFontBefore) > 0.5;

            // 滚动子模式 + 阅读位置：切到滚动、滚到 50%，存盘后关窗。
            // 注意：这里要「等布局真正可滚」——刚切到滚动子模式 / 刚重排完那次，ScrollableHeight 还是 0，
            // 不能一次判定失败（以前就在同一同步回合里判定）。
            bool scrolledToMid = false;
            double novelScrollableHeight = -1;
            var scrollSw = System.Diagnostics.Stopwatch.StartNew();
            while (scrollSw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (reader.TestNovelScrollToFraction(0.5))
                {
                    await Task.Delay(500);          // 等滚动位置真正落到 ScrollViewer 上
                    if (Math.Abs(reader.NovelActualScrollFraction - 0.5) < 0.05)
                    {
                        scrolledToMid = true;
                        novelScrollableHeight = reader.NovelScrollableHeight;
                        break;
                    }
                }
                await Task.Delay(250);
            }
            if (!scrolledToMid) novelScrollableHeight = reader.NovelScrollableHeight;
            int novelRestoreChapterBefore = reader.NovelChapterIndex;
            double fractionBefore = reader.NovelScrollFraction;
            reader.TestFlushSave();
            await Task.Delay(300);
            reader.Close();

            // 断言 A：恢复进度（章节号）。用轮询代替固定 2200ms 等待：重开时 GetProgress 读 sqlite、
            // TxtParser 解析、文档构建都是异步的，固定等待在慢机器上会“isReady 了但章节还没落位”。
            var novelReader2 = new ReaderWindow(novel) { ShowInTaskbar = false };
            novelReader2.Show();
            var restoreSw = System.Diagnostics.Stopwatch.StartNew();
            while (restoreSw.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (novelReader2.IsNovelReady && novelReader2.NovelChapterIndex >= novelRestoreChapterBefore) break;
                await Task.Delay(100);
            }
            novelRestoreOk = novelReader2.IsNovelReady && novelReader2.NovelChapterIndex >= novelRestoreChapterBefore;
            novelRestoreDetail =
                $"存盘章节 {novelRestoreChapterBefore + 1} → 重开章节 {novelReader2.NovelChapterIndex + 1} " +
                $"(ready={novelReader2.IsNovelReady}, 总章数={novelReader2.NovelChapterCount})";
            // 不把 novelReader2 关掉：接下来还要在同一个窗口中核对「滚动子模式 + 位置」的实际落盘情况。
            // 断言 B：滚动子模式 + fraction 跨重开恢复。样例短篇（3 章、每章 2 行）根本滚不动，
            // 那种情况下这条断言在本文件里无从验证 → 显式跳过（计入通过，但 detail 里写明 skipped 原因），
            // 而绝不算“通过”。能不能滚得动，由下面生成的 42 万字长篇小说实测。
            if (!scrolledToMid)
            {
                novelScrollRestored = true;
                novelScrollSkipped = true;
                novelScrollRestoreDetail =
                    $"skipped(数据不足，本章不可滚动 ScrollableHeight={novelScrollableHeight:F0}) — " +
                    "滚动子模式与滚动位置改由生成的长篇小说验证";
            }
            else
            {
                novelScrollRestored = novelReader2.NovelSubMode == "scroll"
                    && novelReader2.NovelScrollFraction > 0.25;
                novelScrollRestoreDetail =
                    $"存盘时 子模式=scroll 位置={fractionBefore:F2} → " +
                    $"重开时 子模式={novelReader2.NovelSubMode} 位置={novelReader2.NovelScrollFraction:F2}";
            }
            novelScrollRestoreDetail += novelScrollSkipped ? " [skipped]" : "";
            novelReader2.Close();
        }

        // 长篇多章 TXT（现场生成在 outDir，绝不改 samples/）：验证「滚动子模式 + fraction 跨重开恢复」。
        // 样例轻小说只有 183 字符、每章两行，一屏就能显示完，ScrollableHeight 恒 0 → 滚不动；
        // 所以必须用真正能滚动的数据来测，否则这条断言永远是假绿或永远红。
        var longNovelBook = await EnsureLongNovelBookAsync(outDir);
        if (longNovelBook is not null)
        {
            var lr = new ReaderWindow(longNovelBook) { ShowInTaskbar = false };
            lr.Show();
            var longOpenSw = System.Diagnostics.Stopwatch.StartNew();
            while (!lr.IsNovelReady && longOpenSw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
            longNovelReaderOk = lr.IsNovelReady;
            longNovelOpenProbe = lr.NovelProbeDetail + " || " + lr.NovelScrollDocDiagnostics;
            // 造的数据本身要合格：必须被 TxtParser 切成 >= 2 章，否则"章节 +1 恢复"无从判断
            longNovelGenOk = lr.NovelChapterCount >= 2;
            if (!longNovelGenOk)
                longNovelOpenProbe += $" || 生成数据不合格(解析出 {lr.NovelChapterCount} 章，应为 >=2)";

            // 断言真实 WPF 页号：只检查 _novelPage 会漏掉 GoToPage 的 1-based 边界错误。
            lr.NovelModePaged.IsChecked = true;
            lr.TestNovelGoToChapter(1);
            var pageLayoutWait = System.Diagnostics.Stopwatch.StartNew();
            while (!lr.IsNovelChapterLayoutStable && pageLayoutWait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
            await Task.Delay(200);
            int actualPages = lr.NovelDisplayedPageCount;
            bool startsAtFirst = lr.NovelPage == 0 && lr.NovelDisplayedPageNumber == 1;
            string firstPageProbe = $"{lr.NovelPage}/{lr.NovelDisplayedPageNumber}";
            lr.TestNovelNextPage();
            await Task.Delay(300);
            bool secondPageShown = lr.NovelPage == 1 && lr.NovelDisplayedPageNumber == 2;
            string secondPageProbe = $"{lr.NovelPage}/{lr.NovelDisplayedPageNumber}";
            for (int page = 2; page < actualPages; page++) lr.TestNovelNextPage();
            await Task.Delay(300);
            bool lastPageShown = lr.NovelChapterIndex == 1 && lr.NovelDisplayedPageNumber == actualPages;
            string lastPageProbe = $"章{lr.NovelChapterIndex} {lr.NovelPage}/{lr.NovelDisplayedPageNumber}";
            lr.TestNovelNextPage();
            await Task.Delay(600);
            novelActualPagingOk = actualPages >= 3 && startsAtFirst && secondPageShown && lastPageShown
                && lr.NovelChapterIndex == 2 && lr.NovelPage == 0 && lr.NovelDisplayedPageNumber == 1;
            novelActualPagingDetail = $"总页数{actualPages}; 起始{firstPageProbe}; 下一页{secondPageProbe}; 末页{lastPageProbe}; 接章{lr.NovelChapterIndex} {lr.NovelPage}/{lr.NovelDisplayedPageNumber}";

            // 先落到第 3 章：章节号必须 > 0，否则"恢复进度"退化成"是不是第 1 章"没有区分度。
            lr.TestNovelGoToChapter(2);
            await Task.Delay(700);

            // 翻页子模式 → 滚动子模式；并轮询等布局真正可滚（刚切到滚动/刚换章时 ScrollableHeight 还是 0）
            bool scrolledLong = false;
            var longScrollSw = System.Diagnostics.Stopwatch.StartNew();
            while (longScrollSw.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (lr.TestNovelScrollToFraction(0.5))
                {
                    await Task.Delay(500);
                    if (Math.Abs(lr.NovelActualScrollFraction - 0.5) < 0.05) { scrolledLong = true; break; }
                }
                await Task.Delay(200);
            }
            longNovelScrollableHeight = lr.NovelScrollableHeight;
            longNovelScrollProbe = lr.NovelScrollDocDiagnostics;
            // 只操作真实视口，不能用同步字段/FlushSave 的测试钩子掩盖章内滚动未自动保存。
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (lr.IsNovelScrollRestoring && settle.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
            // 等前面的 50% 滚动触发的防抖保存结束，否则旧计时器也可能保存后面的 65%，造成假阳性。
            await Task.Delay(2300);
            var beforeRawScroll = App.Library.GetProgress(longNovelBook.Id);
            bool baselineSaved = beforeRawScroll is not null && Math.Abs(beforeRawScroll.ScrollOffset - 0.5) < 0.08;
            bool rawScroll = lr.TestNovelScrollViewportToFraction(0.65);
            await Task.Delay(2600);
            var autoSaved = App.Library.GetProgress(longNovelBook.Id);
            novelScrollAutosaveOk = baselineSaved && rawScroll && autoSaved is not null
                && autoSaved.ChapterIndex == lr.NovelChapterIndex
                && Math.Abs(autoSaved.ScrollOffset - 0.65) < 0.08;
            // 后面的既有恢复断言仍使用 50% 作为目标。
            lr.TestNovelScrollToFraction(0.5);
            await Task.Delay(400);
            longNovelChapterBefore = lr.NovelChapterIndex;
            longNovelScrollFractionBefore = lr.NovelActualScrollFraction;

            // 让滚动位置被真实记录下来（产品里滚动/翻页/滚轮都会触发 UpdateNovelInfo 刷新 _novelScrollFraction）
            await Task.Delay(400);
            lr.TestFlushSave();
            await Task.Delay(300);
            longNovelSaveProbe = DescribeProgress(longNovelBook.Id, "flush 后");
            lr.Close();
            longNovelSaveProbe += " || " + DescribeProgress(longNovelBook.Id, "close 后");

            var lr2 = new ReaderWindow(longNovelBook) { ShowInTaskbar = false };
            longNovelSaveProbe += " || 构造后 " + DescribeProgress(longNovelBook.Id, "getprogress");
            lr2.Show();
            var lr2Sw = System.Diagnostics.Stopwatch.StartNew();
            while (lr2Sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (lr2.IsNovelReady && lr2.NovelSubMode == "scroll") break;
                await Task.Delay(100);
            }
            await Task.Delay(600);   // 让恢复滚动的重试回合走完（文档没量出高度时逐帧重试）
            longNovelChapterAfter = lr2.NovelChapterIndex;
            longNovelScrollFractionAfter = lr2.NovelScrollFraction;
            longNovelActualScrollFractionAfter = lr2.NovelActualScrollFraction;
            longNovelSubModeAfter = lr2.NovelSubMode;
            // TRACE 只保留最后 6 跳（真正有用的就是"恢复章节被谁覆盖"），避免 summary 爆行。
            var traceTail = lr2.NovelTrace.Skip(Math.Max(0, lr2.NovelTrace.Count - 6)).ToList();
            longNovelReopenProbe = lr2.NovelProbeDetail + " || " + lr2.NovelScrollDocDiagnostics +
                                   $" || scrollable={lr2.NovelScrollableHeight:F0} || TRACE(尾6): " + string.Join(" ⟵ ", traceTail);
            // 硬断言：滚得动还不够，重开后必须真的回到滚动子模式 + 同一章 + 相近位置。
            // 任何一项不满足都必须是 False（不能把失败吞掉）。
            var cond = new (string Name, bool Ok)[]
            {
                ("readerOk", longNovelReaderOk),
                ("genOk", longNovelGenOk),
                ("scrolledLong", scrolledLong),
                ("isReady", lr2.IsNovelReady),
                ("subModeScroll", longNovelSubModeAfter == "scroll"),
                ("sameChapter", longNovelChapterAfter == longNovelChapterBefore),
                ("chapterGt0", longNovelChapterBefore > 0),
                ("recFrac", Math.Abs(lr2.NovelScrollFraction - 0.5) < 0.15),
                ("actFrac", Math.Abs(longNovelActualScrollFractionAfter - 0.5) < 0.15),
            };
            longNovelScrollRestoredOk = cond.All(c => c.Ok);
            string failedConds = string.Join(",", cond.Where(c => !c.Ok).Select(c => c.Name));
            // 短篇那本若滚不动而走了 skipped 分支，就用这里的结论作为最终判定 —— 判定不会被削弱：
            // 长篇小说滚不动（数据不足）时 longNovelScrollRestoredOk 为 False，冒烟照样红。
            novelScrollRestored = longNovelScrollRestoredOk && novelScrollRestored;
            // summary 主行只保留判据本身；大段 PROBE/TRACE 诊断放在失败时才展开，且整体截断，
            // 否则一行几千字根本没法看。
            longNovelScrollRestoreDetail =
                $"滚到 {longNovelScrollFractionBefore:F2}(可滚 {longNovelScrollableHeight:F0}px, {longNovelChapterBefore + 1} 章) → " +
                $"重开 子模式={longNovelSubModeAfter} 章节 {longNovelChapterAfter + 1} " +
                $"位置 {longNovelScrollFractionAfter:F2}/实滚 {longNovelActualScrollFractionAfter:F2}" +
                (longNovelScrollRestoredOk ? "" : $" | 未通过: {failedConds}");
            if (!longNovelScrollRestoredOk)
            {
                string diag = $"open: {longNovelOpenProbe} | scroll: {longNovelScrollProbe} | reopen: {longNovelReopenProbe} | " +
                              $"DB: {longNovelSaveProbe}";
                longNovelScrollRestoreDetail += " | 诊断: " + Truncate(diag, 200);
            }
            lr2.Close();
        }
        else
        {
            longNovelScrollRestoreDetail = "无法生成长篇小说样例，滚动子模式持久化无法验证";
            novelScrollRestored = false;
        }

        shortNovelCompleteOk = await CheckShortNovelCompletionAsync(outDir);
        var novelPreferences = longNovelBook is null
            ? (Formatting: false, Wheel: false, Detail: "缺少长篇小说样例")
            : await CheckNovelPreferencesAsync(longNovelBook);
        var novelExpansion = await CheckNovelExpansionAsync(outDir);

        // PDF 单独导入并打开（单文件发布时 pdfium.dll 需可被加载）
        string pdfPath = Directory.GetFiles(samplesDir, "*.pdf", SearchOption.AllDirectories).FirstOrDefault() ?? "";
        if (pdfPath.Length > 0)
        {
            var pdfResult = await App.Importer.ImportAsync(new[] { pdfPath });
            var pdfBook = App.Library.GetBookByPath(Path.GetFullPath(pdfPath));
            if (pdfBook is not null)
            {
                var reader = new ReaderWindow(pdfBook) { ShowInTaskbar = false };
                reader.Show();
                await Task.Delay(2000);
                reader.TestSwitchToPaged();
                await Task.Delay(700);
                pdfReaderOk = reader.IsComicPageLoaded;
                Capture(reader, Path.Combine(outDir, "reader-pdf.png"));
                reader.Close();
            }
        }

        // 200 页长 PDF：打开过程不应阻塞界面（旧实现会在 UI 线程逐页调用渲染器）
        string longPdf = CreateLongPdf(outDir, 200);
        await App.Importer.ImportAsync(new[] { longPdf });
        var longPdfBook = App.Library.GetBookByPath(Path.GetFullPath(longPdf));
        if (longPdfBook is not null)
        {
            stall.Start();
            var openSw = System.Diagnostics.Stopwatch.StartNew();
            var reader = new ReaderWindow(longPdfBook) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1200);
            reader.TestSwitchToPaged();
            await Task.Delay(800);
            openSw.Stop();
            longPdfOpenSeconds = openSw.Elapsed.TotalSeconds;
            longPdfStallMs = stall.MaxStallMs;
            stall.Stop();
            reader.Close();
        }

        // 超长条漫 PDF：验证“翻页 → 条漫”切换不卡死（带超时保护）
        string tallPdf = Environment.GetEnvironmentVariable("COMICVERSE_SMOKE_TALL_PDF") ?? "";
        if (tallPdf.Length > 0 && File.Exists(tallPdf))
        {
            var tallResult = await App.Importer.ImportAsync(new[] { tallPdf });
            var tallBook = App.Library.GetBookByPath(Path.GetFullPath(tallPdf));
            if (tallBook is not null)
            {
                var reader = new ReaderWindow(tallBook) { ShowInTaskbar = false };
                reader.Show();
                await Task.Delay(1500);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                reader.TestSwitchToWebtoon();
                while (!reader.IsWebtoonReady && sw.Elapsed < TimeSpan.FromSeconds(20))
                    await Task.Delay(100);
                sw.Stop();
                tallWebtoonSeconds = sw.Elapsed.TotalSeconds;
                tallWebtoonOk = reader.IsWebtoonReady && reader.WebtoonPageCount > 1;
                readerWebtoonPages = reader.WebtoonPageCount;
                Capture(reader, Path.Combine(outDir, "tall-pdf-webtoon.png"));
                reader.Close();
            }
        }

        await Task.Delay(400);
        Capture(main, Path.Combine(outDir, "shelf.png"));
        bool lightHeaderOk = true;
        try
        {
            // 浅色主题下的书架：顶栏文字必须在浅色底上依然可读（此前是白字白底）
            ThemeService.Toggle();
            await Task.Delay(500);
            Capture(main, Path.Combine(outDir, "shelf-light.png"));
            lightHeaderOk = HeaderHasContrast(main);
            ThemeService.Toggle();
            await Task.Delay(300);
        }
        catch
        {
            lightHeaderOk = false;
        }
        var logicAudit = await CheckLogicAuditAsync(main, outDir);
        main.Close();

        string summary =
            $"SMOKE 完成 | 样例目录: {samplesDir}\n" +
            $"导入: 新增 {result.Imported}, 更新 {result.Updated}, 失败 {result.Failed.Count}\n" +
            $"书架: {App.Library.GetBooks().Count} 本 (漫画 {comic is not null}, 小说 {novel is not null})\n" +
            $"漫画翻页: {comicReaderOk} | 快速翻页: {pagingOk} | 远跳: {farJumpOk} (页 {farJumpPage}) | 条漫: {webtoonOk} | 条漫滚动: {webtoonScrollOk} | 条漫远跳: {webtoonFarJumpOk} | 双页: {doubleOk} | 小说: {novelReaderOk} | PDF: {pdfReaderOk}\n" +
            $"条漫拖进度条: {sliderDragOk} (落到第 {sliderDragPage + 1} 页，视口 {sliderDragRange}，偏移 {sliderDragOffset:F0}，内容占比 {sliderDragInk * 100:F1}%，空白条 {sliderDragBlank}，出图耗时 {sliderDragMs / 1000:F1}s)\n" +
            $"默认阅读方式: 条漫={defaultModeOk} 打开即条漫={defaultOpensWebtoon}\n" +
            $"按书记忆: 翻页模式={modePersisted} 缩放150%={zoomPersisted} 比例数字={zoomTextRestored}\n" +
            $"缩放同步: 比例数字={zoomTextSyncOk}\n" +
            $"小说: 翻页进度={novelProgressOk} 滚动接章={novelScrollChapterOk} 浅色背景={novelLightThemeOk} 恢复进度={novelRestoreOk} — {novelRestoreDetail}\n" +
            $"双页→翻页游标: {doubleToPagedOk} — {doubleToPagedDetail}\n" +
            $"双页缩放: {doubleZoomOk} | RTL 左右页: {doubleRtlOrderOk} — {doubleRtlDetail}\n" +
            $"双页缩放重启恢复: {doubleZoomRestored}（保存 {doubleZoomSaved:F2}）\n" +
            $"小说子模式持久化: {novelScrollRestored} — {novelScrollRestoreDetail}\n" +
            $"长篇多章滚动恢复: {longNovelScrollRestoredOk} — {longNovelScrollRestoreDetail}\n" +
            $"排版字号: 修改后保留={novelFontSizeKept} 未污染全局默认={!novelFontSizeLeaked}\n" +
            $"小说方向键（← 上一章 / → 下一章，RTL=1）: {novelArrowOk} — {novelArrowDetail}\n" +
            $"关闭阅读器耗时: {closeSeconds:F1}s\n" +
            $"界面卡顿: 打开漫画最大停顿 {openStallMs:F0}ms | 整个阅读过程最大停顿 {sessionStallMs:F0}ms\n" +
            $"浅色主题顶栏对比度: {lightHeaderOk}\n" +
            $"导入后书架立即刷新: {shelfRefreshOk}\n" +
            $"书架改进: 空库提示={shelfEmptyOk} 阅读状态与继续阅读={shelfChecks.State} 搜索/筛选无结果提示={shelfChecks.Empty} 打开时重置进度={shelfChecks.Reset}\n" +
            $"小说实际分页与章末接章: {novelActualPagingOk}（{novelActualPagingDetail}） | 章内滚动自动保存: {novelScrollAutosaveOk}\n" +
            $"短篇小说一屏读完（翻页/滚动）: {shortNovelCompleteOk}\n" +
            $"小说排版重开恢复: {novelPreferences.Formatting} | 滚轮速度与跨章: {novelPreferences.Wheel} — {novelPreferences.Detail}\n" +
            $"独立QA：排版扩展={novelExpansion.Spacing} 正文查找={novelExpansion.Search} — {novelExpansion.Detail}\n" +
            $"独立QA：整体逻辑={logicAudit.Ok} — {logicAudit.Detail}\n" +
            $"不等高漫画翻页切条漫保持页码: {unequalWebtoonOk}\n" +
            $"条漫页间接缝检测（背景色行数）: {webtoonSeamRows}\n" +
            $"条漫鼠标拖拽滑动: {webtoonDragOk}\n" +
            $"条漫单击释放鼠标（捕获控件事件路由）: {webtoonClickReleaseOk} — {webtoonClickDetail}\n" +
            $"条漫放大后横向平移: {webtoonHorizontalPanOk}（可滚动宽度 {webtoonHorizontalScrollableWidth:F0}px）\n" +
            $"条漫进度比例: {webtoonProgressOk} | 切出再回位置: {webtoonModeReentryOk}\n" +
            $"条漫方向键（← 上一页 / → 下一页）: {webtoonArrowOk} — {webtoonArrowDetail}\n" +
            (longPdfOpenSeconds >= 0 ? $"200 页 PDF: 打开耗时 {longPdfOpenSeconds:F1}s，最大停顿 {longPdfStallMs:F0}ms\n" : "") +
            (tallPdf.Length > 0 ? $"超长 PDF 条漫切换: {tallWebtoonOk}（耗时 {tallWebtoonSeconds:F1}s，页数 {readerWebtoonPages}）\n" : "") +
            (webtoonOk ? "条漫诊断: " + readerWebtoonStats + "\n" : "") +
            (comicError is null ? "" : "异常: " + comicError + "\n") +
            $"截图目录: {outDir}";

        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary);

        bool tallOk = tallPdf.Length == 0 || tallWebtoonOk;
        bool closeOk = closeSeconds >= 0 && closeSeconds < 3;
        // 界面停顿阈值（毫秒）：打开与阅读过程中都不应出现可感知的长时间卡死
        bool stallOk = openStallMs >= 0 && openStallMs < 1500 && sessionStallMs < 2500 && (longPdfStallMs < 0 || longPdfStallMs < 1500);
        return comic is not null && novel is not null && comicReaderOk && pagingOk && farJumpOk &&
               shelfEmptyOk && shelfChecks.State && shelfChecks.Empty && shelfChecks.Reset &&
               novelActualPagingOk && novelScrollAutosaveOk &&
               shortNovelCompleteOk &&
               novelPreferences.Formatting && novelPreferences.Wheel &&
               novelExpansion.Spacing && novelExpansion.Search &&
               logicAudit.Ok &&
               unequalWebtoonOk &&
               lightHeaderOk && shelfRefreshOk && webtoonSeamRows == 0 &&
               webtoonDragOk && webtoonClickReleaseOk &&
               webtoonHorizontalPanOk &&
               webtoonProgressOk && webtoonModeReentryOk &&
               webtoonArrowOk &&
               webtoonOk && webtoonScrollOk && webtoonFarJumpOk &&
               sliderDragOk &&
               doubleOk && novelReaderOk && pdfReaderOk && tallOk && closeOk && stallOk && defaultModeOk && defaultOpensWebtoon &&
               modePersisted && zoomPersisted && zoomTextSyncOk && zoomTextRestored &&
               doubleToPagedOk && novelScrollRestored && novelFontSizeKept && !novelFontSizeLeaked &&
               doubleZoomOk && doubleRtlOrderOk &&
               doubleZoomRestored &&
               novelProgressOk && novelScrollChapterOk && novelLightThemeOk && novelRestoreOk && novelArrowOk ? 0 : 1;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CreateUnobservedTaskForSafetyCheck()
        => _ = Task.FromException(new InvalidOperationException("QA: intentional unobserved task failure"));

    private static async Task<bool> CheckShortNovelCompletionAsync(string outDir)
    {
        string file = Path.Combine(outDir, "短篇一屏测试.txt");
        await File.WriteAllTextAsync(file, "第1章 星光\n\n少女抬起头，看见一颗星星划过夜空。\n", new System.Text.UTF8Encoding(false));
        await App.Importer.ImportAsync([file]);
        var book = App.Library.GetBookByPath(file);
        if (book is null) return false;
        var reader = new ReaderWindow(book) { ShowInTaskbar = false };
        try
        {
            reader.Show();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while ((!reader.IsNovelReady || !reader.IsNovelChapterLayoutStable) && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(100);
            reader.TestFlushSave();
            bool pagedDone = reader.IsNovelChapterLayoutStable && reader.NovelDisplayedPageCount == 1
                && App.Library.GetBook(book.Id)?.Progress == 1;
            reader.NovelModeScroll.IsChecked = true;
            wait.Restart();
            while (!reader.IsNovelChapterLayoutStable && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
            reader.TestFlushSave();
            var progress = App.Library.GetProgress(book.Id);
            return pagedDone && reader.IsNovelChapterLayoutStable && reader.NovelSubMode == "scroll"
                && App.Library.GetBook(book.Id)?.Progress == 1 && progress?.ScrollOffset == 0;
        }
        finally { reader.Close(); }
    }

    private static async Task<bool> CheckUnequalWebtoonSwitchAsync(string outDir)
    {
        string file = Path.Combine(outDir, "unequal-pages.cbz");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            int[] heights = [200, 2400, 500, 1800, 700];
            for (int i = 0; i < heights.Length; i++)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(40 + i * 35), 90, 180)), null, new Rect(0, 0, 480, heights[i]));
                var bitmap = new RenderTargetBitmap(480, heights[i], 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var png = new MemoryStream();
                encoder.Save(png);
                png.Position = 0;
                using var stream = zip.CreateEntry($"page{i:00}.png").Open();
                png.CopyTo(stream);
            }
        }
        await App.Importer.ImportAsync([file]);
        var book = App.Library.GetBookByPath(file);
        if (book is null) return false;
        App.Library.SaveProgress(book.Id, 0.4, 2, 0, 0, "paged", 1);
        var reader = new ReaderWindow(book) { ShowInTaskbar = false };
        try
        {
            reader.Show();
            await Task.Delay(1500);
            reader.TestSwitchToWebtoon();
            await Task.Delay(1700);
            bool firstSwitch = reader.IsWebtoonReady && reader.WebtoonCurrentPage == 2;
            reader.TestSwitchToPaged();
            await Task.Delay(300);
            reader.TestJumpToPage(3);
            await Task.Delay(400);
            reader.TestSwitchToWebtoon();
            await Task.Delay(600);
            Capture(reader, Path.Combine(outDir, "unequal-mode-switch.png"));
            return firstSwitch && reader.WebtoonCurrentPage == 3;
        }
        finally { reader.Close(); }
    }

    private static async Task<(bool State, bool Empty, bool Reset)> CheckShelfImprovementsAsync(MainWindow main, string outDir)
    {
        var books = App.Library.GetBooks(filter: "comic").OrderBy(b => b.PageCount).Take(3).ToList();
        if (books.Count < 3) return (false, false, false);
        long missingId = 0;
        try
        {
            foreach (var book in books) App.Library.ResetProgress(book.Id);
            App.Library.SaveProgress(books[1].Id, 0, 0, 0, 0, "paged", 1.2);
            App.Library.SaveProgress(books[2].Id, 1, books[2].PageCount - 1, 0, 0, "paged", 1.4);
            missingId = App.Library.UpsertBook(new Book
            {
                Title = "已失联的继续阅读测试", FilePath = Path.Combine(outDir, "not-present.cbz"),
                Type = BookType.Comic, Format = BookFormat.Cbz, PageCount = 10
            });
            App.Library.SaveProgress(missingId, 0.3, 3, 0, 0);
            main.TestSetShelfQuery("", 1);
            bool unread = main.VisibleBookIds.Contains(books[0].Id) && !main.VisibleBookIds.Contains(books[1].Id);
            main.TestSetShelfQuery("", 2, "comic");
            bool inProgress = main.VisibleBookIds.Contains(books[1].Id) && !main.VisibleBookIds.Contains(books[0].Id)
                && !main.VisibleBookIds.Contains(books[2].Id) && main.ContinueBookId == books[1].Id;
            Capture(main, Path.Combine(outDir, "shelf-reading.png"));
            main.Width = 980;
            await Task.Delay(200);
            Capture(main, Path.Combine(outDir, "shelf-minwidth.png"));
            main.Width = 1280;
            await Task.Delay(200);
            main.TestSetShelfQuery("", 3);
            bool completed = main.VisibleBookIds.Contains(books[2].Id) && !main.VisibleBookIds.Contains(books[1].Id);
            main.TestSetShelfQuery("__no_such_book_71d7__", 0);
            bool searchEmpty = main.ShelfCount == 0 && main.EmptyHeadingText == "未找到匹配的书籍";
            Capture(main, Path.Combine(outDir, "shelf-search-empty.png"));
            main.TestSetShelfQuery("", 3, "novel");
            bool filterEmpty = main.ShelfCount == 0 && main.EmptyHeadingText == "当前筛选下没有书籍";

            // 真实打开阅读器后执行和右键菜单同一方法，验证 Closing 保存不会撤销重置。
            var resetReader = new ReaderWindow(books[2]) { Owner = main, ShowInTaskbar = false };
            resetReader.Show();
            await Task.Delay(1800);
            resetReader.TestFlushSave();
            main.TestResetProgress(books[2].Id);
            var p = App.Library.GetProgress(books[2].Id);
            var resetBook = App.Library.GetBook(books[2].Id);
            bool resetOk = !resetReader.IsVisible && p is not null && p.PageIndex == 0 && p.ScrollOffset == 0
                && p.ChapterIndex == 0 && p.Mode == "paged" && Math.Abs(p.Zoom - 1.4) < 0.05
                && resetBook?.Progress == 0 && resetBook.LastReadTime == default;
            return (unread && inProgress && completed, searchEmpty && filterEmpty, resetOk);
        }
        catch (Exception ex)
        {
            Log.Error("书架改进冒烟失败", ex);
            return (false, false, false);
        }
        finally
        {
            foreach (var book in books) App.Library.ResetProgress(book.Id);
            if (missingId > 0) App.Library.RemoveBook(missingId);
            main.TestSetShelfQuery("", 0);
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        int w = Math.Max(1, (int)window.ActualWidth);
        int h = Math.Max(1, (int)window.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>顶栏是否有足够对比：同一区域同时存在明显亮暗像素（防止白字白底这类问题）。</summary>
    private static bool HeaderHasContrast(Window window)
    {
        window.UpdateLayout();
        int w = Math.Max(1, (int)window.ActualWidth);
        int h = Math.Max(1, (int)window.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        int stride = w * 4;
        var buf = new byte[stride * h];
        rtb.CopyPixels(buf, stride, 0);
        int min = 255, max = 0;
        for (int y = 6; y < Math.Min(56, h); y += 2)
        {
            for (int x = 6; x < w - 6; x += 3)
            {
                int i = y * stride + x * 4;
                int lum = (buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8;
                if (lum < min) min = lum;
                if (lum > max) max = lum;
            }
        }
        return max - min > 60;
    }

    /// <summary>条漫内容区里“漏出背景色”的行数：相邻页之间若有细白线/缝隙，就会出现这种行。</summary>
    private static int BackgroundRowCount(Window window)
    {
        window.UpdateLayout();
        int w = Math.Max(1, (int)window.ActualWidth);
        int h = Math.Max(1, (int)window.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        int stride = w * 4;
        var buf = new byte[stride * h];
        rtb.CopyPixels(buf, stride, 0);
        var bg = (window.TryFindResource("ReaderBgBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var viewer = VisualTreeHelpers.FindVisualChild<WebtoonViewer>(window);
        if (viewer is null || viewer.ActualWidth < 1 || viewer.ActualHeight < 1) return -1;
        var viewerTopLeft = viewer.TranslatePoint(new Point(0, 0), window);
        int x = Math.Clamp((int)(viewerTopLeft.X + viewer.ActualWidth / 2), 1, w - 2);
        int yStart = Math.Clamp((int)viewerTopLeft.Y + 4, 0, h - 1);
        int yEnd = Math.Clamp((int)(viewerTopLeft.Y + viewer.ActualHeight) - 4, yStart, h);
        int bad = 0;
        for (int y = yStart; y < yEnd; y++)
        {
            int i = y * stride + x * 4;
            int diff = Math.Abs(buf[i] - bg.B) + Math.Abs(buf[i + 1] - bg.G) + Math.Abs(buf[i + 2] - bg.R);
            if (diff < 24) bad++;
        }
        return bad;
    }

    /// <summary>内容区“有内容”的比例：与背景色差异明显的像素占比，用于判断是否真的空白。</summary>
    private static double ContentInkRatio(Window window)
    {
        window.UpdateLayout();
        int w = Math.Max(1, (int)window.ActualWidth);
        int h = Math.Max(1, (int)window.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        int stride = w * 4;
        var buf = new byte[stride * h];
        rtb.CopyPixels(buf, stride, 0);
        // 以阅读器背景色为基准：空白区域就是一块背景色，加载出图后像素与背景差异明显
        var bg = (window.TryFindResource("ReaderBgBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        byte rb = bg.B, rg = bg.G, rr = bg.R;
        long ink = 0, total = 0;
        int x0 = (int)(w * 0.25), x1 = (int)(w * 0.75);
        int y0 = (int)(h * 0.2), y1 = (int)(h * 0.8);
        for (int y = y0; y < y1; y += 3)
        {
            for (int x = x0; x < x1; x += 3)
            {
                int i = y * stride + x * 4;
                total++;
                int diff = Math.Abs(buf[i] - rb) + Math.Abs(buf[i + 1] - rg) + Math.Abs(buf[i + 2] - rr);
                if (diff > 90) ink++;
            }
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static string CreateLongComic(string outDir, int pageCount = 120, string fileName = "long-comic.cbz")
    {
        string dir = Path.Combine(outDir, "long-gen");
        Directory.CreateDirectory(dir);
        var pages = new List<string>();
        for (int i = 1; i <= pageCount; i++)
        {
            string p = Path.Combine(dir, $"p{i:000}.png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(30 + i * 7), 60, 200)), null, new Rect(0, 0, 480, 880));
                var ft = new FormattedText($"P{i}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Arial"), 28, System.Windows.Media.Brushes.White, 1.0);
                dc.DrawText(ft, new Point(24, 420));
            }
            var rtb = new RenderTargetBitmap(480, 880, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(p);
            enc.Save(fs);
            pages.Add(p);
        }
        string cbz = Path.Combine(outDir, fileName);
        using var cz = File.Create(cbz);
        using var zip = new ZipArchive(cz, ZipArchiveMode.Create);
        for (int i = 0; i < pages.Count; i++)
        {
            var entry = zip.CreateEntry($"pages/p{i + 1:000}.png", CompressionLevel.Optimal);
            using var es = entry.Open();
            using var ist = File.OpenRead(pages[i]);
            ist.CopyTo(es);
        }
        return cbz;
    }

    /// <summary>
    /// 现场生成一本"几千字、多章"的长篇 TXT（写到 outDir，绝不碰仓库 samples/），导入后返回书，
    /// 用于验证小说滚动子模式 + 滚动位置能跨重开恢复。样例短篇只有 183 字符、每章两行，一屏显示完，
    /// ScrollableHeight 恒 0 —— 那种数据上"滚到 50%"物理上不可能成立，不能用来判定子模式恢复。
    /// </summary>
    private static async Task<Book?> EnsureLongNovelBookAsync(string outDir)
    {
        try
        {
            string dir = Path.Combine(outDir, "novel-gen");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "长篇多章·滚动测试.txt");
            // 每章 30 段 × 约 160 字 ≈ 4800 字，12 章共约 5.8 万字：在字号 12~32、1360x900 的窗口下
            // 单章都远超一屏（实测单章流文档需滚动数倍视口高度），"滚到 50%"才有物理意义。
            const int chapters = 12;
            const int paragraphsPerChapter = 30;
            const int charsPerParagraph = 160;
            const string filler =
                "深蓝色的夜空下，少女站在灯塔顶端，望着远方海平线上缓缓亮起的星光，心里反复念着那个约定。" +
                "少年背着一只旧皮箱站在船头，等待着未知的旅程，海雾散开的那一刻，潮水拍岸的声音传得很远很远。";
            var sb = new System.Text.StringBuilder();
            sb.Append("星海旅人（长篇版）\n\n");       // 首个标题之前算前言
            for (int c = 1; c <= chapters; c++)
            {
                sb.Append($"第{c}章 起航第{c}日\n\n");
                for (int p = 1; p <= paragraphsPerChapter; p++)
                {
                    var para = new System.Text.StringBuilder($"第{c}章第{p}段。");
                    int seed = c * 131 + p * 17;                 // 每段起笔位置不同，读起来不像复制粘贴
                    while (para.Length < charsPerParagraph)
                        para.Append(filler[(seed++ % 10)..][..Math.Min(filler.Length - seed % 10, 32)]);
                    sb.Append(para).Append('\n');
                    if (p % 5 == 0) sb.Append('\n');             // 少量空行，保持真实的段落节奏
                }
                sb.Append('\n');
            }
            await File.WriteAllTextAsync(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            await App.Importer.ImportAsync(new[] { path });
            return App.Library.GetBookByPath(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            Log.Error("生成/导入长篇小说样例失败", ex);
            return null;
        }
    }

    /// <summary>把过长的诊断字符串截断，避免 summary 一行几千字。</summary>
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text!.Length <= max ? text : text[..max] + $"…(截断,原长 {text.Length})";
    }

    /// <summary>直接查库里的阅读进度（不经阅读器内存字段），用于分辨"保存丢"还是"恢复丢"。</summary>
    private static string DescribeProgress(long bookId, string tag)
    {
        try
        {
            var p = App.Library.GetProgress(bookId);
            return p is null
                ? $"{tag}:<null>"
                : $"{tag}:ch={p.ChapterIndex} page={p.PageIndex} scroll={p.ScrollOffset:F2} mode='{p.Mode}'";
        }
        catch (Exception ex)
        {
            return $"{tag}:ERR {ex.Message}";
        }
    }

    /// <summary>生成多页 PDF，用于验证打开长 PDF 时界面不被阻塞。</summary>
    private static string CreateLongPdf(string outDir, int pageCount)
    {
        const int pageWidth = 612;
        const int pageHeight = 792;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}] /Count {pageCount} >>"
        };
        int fontObj = pageCount * 2 + 3;
        for (int i = 0; i < pageCount; i++)
        {
            int contentObj = pageCount + 3 + i;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
        }
        for (int i = 0; i < pageCount; i++)
        {
            string streamText = $"BT /F1 20 Tf 72 720 Td (Page {i + 1}) Tj ET";
            objects.Add($"<< /Length {System.Text.Encoding.ASCII.GetByteCount(streamText)} >>\nstream\n{streamText}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        long xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        string path = Path.Combine(outDir, "long-200p.pdf");
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    /// <summary>界面卡顿探针：记录 UI 线程两次心跳之间的最大间隔。</summary>
    private sealed class StallProbe
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly DispatcherTimer _timer;
        private double _last;
        private double _max;

        public StallProbe()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(40) };
            _timer.Tick += (_, _) =>
            {
                double now = _sw.Elapsed.TotalMilliseconds;
                if (_last > 0)
                {
                    double gap = now - _last;
                    if (gap > _max) _max = gap;
                }
                _last = now;
            };
        }

        public double MaxStallMs => _max;

        public void Start()
        {
            _max = 0;
            _last = 0;
            _sw.Restart();
            _timer.Start();
        }

        public void Reset()
        {
            _max = 0;
            _last = _sw.Elapsed.TotalMilliseconds;
        }

        public void Stop() => _timer.Stop();
    }
}
