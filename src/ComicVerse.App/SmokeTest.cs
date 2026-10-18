using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComicVerse.App.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Models;

namespace ComicVerse.App;

/// <summary>无人工参与的启动冒烟测试：导入样例 → 打开漫画/小说阅读器 → 截图并断言。</summary>
public static class SmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string samplesDir = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a))
                            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));
        string outDir = Environment.GetEnvironmentVariable("SMOKE_OUT_DIR") ?? Path.Combine(Path.GetTempPath(), "comicverse-smoke");
        Directory.CreateDirectory(outDir);

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

        var result = await App.Importer.ImportAsync(new[] { samplesDir });
        // 现场生成一本 30 页长漫画，用于真实覆盖“进度条远距离跳页”
        string longCbz = CreateLongComic(outDir);
        var longResult = await App.Importer.ImportAsync(new[] { longCbz });
        result.Imported += longResult.Imported;
        result.Updated += longResult.Updated;
        result.Failed.AddRange(longResult.Failed);
        main.Refresh();
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
        bool pdfReaderOk = false;
        bool pagingOk = false;
        bool webtoonScrollOk = false;
        bool farJumpOk = false;
        bool webtoonFarJumpOk = false;
        bool webtoonDragOk = false;
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
        string novelSectionError = "";
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
            }
            catch (Exception ex)
            {
                comicError = ex.Message;
            }
            var closeSw = System.Diagnostics.Stopwatch.StartNew();
            reader.Close();
            closeSw.Stop();
            closeSeconds = closeSw.Elapsed.TotalSeconds;

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
            $"小说子模式持久化: {novelScrollRestored} — {novelScrollRestoreDetail}\n" +
            $"长篇多章滚动恢复: {longNovelScrollRestoredOk} — {longNovelScrollRestoreDetail}\n" +
            $"排版字号: 修改后保留={novelFontSizeKept} 未污染全局默认={!novelFontSizeLeaked}\n" +
            $"小说方向键（← 上一章 / → 下一章，RTL=1）: {novelArrowOk} — {novelArrowDetail}\n" +
            $"关闭阅读器耗时: {closeSeconds:F1}s\n" +
            $"界面卡顿: 打开漫画最大停顿 {openStallMs:F0}ms | 整个阅读过程最大停顿 {sessionStallMs:F0}ms\n" +
            $"浅色主题顶栏对比度: {lightHeaderOk}\n" +
            $"导入后书架立即刷新: {shelfRefreshOk}\n" +
            $"条漫页间接缝检测（背景色行数）: {webtoonSeamRows}\n" +
            $"条漫鼠标拖拽滑动: {webtoonDragOk}\n" +
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
               lightHeaderOk && shelfRefreshOk && webtoonSeamRows == 0 &&
               webtoonDragOk &&
               webtoonArrowOk &&
               webtoonOk && webtoonScrollOk && webtoonFarJumpOk &&
               sliderDragOk &&
               doubleOk && novelReaderOk && pdfReaderOk && tallOk && closeOk && stallOk && defaultModeOk && defaultOpensWebtoon &&
               modePersisted && zoomPersisted && zoomTextSyncOk && zoomTextRestored &&
               doubleToPagedOk && novelScrollRestored && novelFontSizeKept && !novelFontSizeLeaked &&
               novelProgressOk && novelScrollChapterOk && novelLightThemeOk && novelRestoreOk && novelArrowOk ? 0 : 1;
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
        int x = w / 2;
        int bad = 0;
        for (int y = (int)(h * 0.12); y < (int)(h * 0.9); y++)
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
