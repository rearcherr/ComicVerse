using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.App.Windows;
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

        var main = new MainWindow();
        main.Show();
        await Task.Delay(900);
        main.Refresh();

        var result = await App.Importer.ImportAsync(new[] { samplesDir });
        main.Refresh();
        var comic = App.Library.GetBooks(filter: "comic").FirstOrDefault();
        var novel = App.Library.GetBooks(filter: "novel").FirstOrDefault();

        bool comicReaderOk = false;
        bool webtoonOk = false;
        bool doubleOk = false;
        bool novelReaderOk = false;
        bool pdfReaderOk = false;
        bool pagingOk = false;
        bool webtoonScrollOk = false;
        bool defaultModeOk = false;
        bool defaultOpensWebtoon = false;
        bool modePersisted = false;
        bool zoomPersisted = false;
        bool zoomTextSyncOk = false;
        bool zoomTextRestored = false;
        bool novelProgressOk = false;
        bool novelLightThemeOk = false;
        bool novelRestoreOk = false;
        double closeSeconds = -1;
        bool tallWebtoonOk = false;
        double tallWebtoonSeconds = -1;
        string? comicError = null;
        string readerWebtoonStats = "";
        int readerWebtoonPages = 0;

        if (comic is not null)
        {
            var reader = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1600);
            reader.TestSwitchToPaged();
            await Task.Delay(700);
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
                reader.TestSwitchToDouble();
                await Task.Delay(1400);
                doubleOk = reader.IsDoubleReady;
                Capture(reader, Path.Combine(outDir, "reader-double.png"));
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
        }

        if (novel is not null)
        {
            var reader = new ReaderWindow(novel) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1800);
            novelReaderOk = reader.IsNovelReady;
            reader.TestNovelNextPage();
            reader.TestNovelNextPage();
            reader.TestNovelNextChapter();
            await Task.Delay(700);
            int novelChapterBefore = reader.NovelChapterIndex;
            novelProgressOk = novelChapterBefore >= 1;
            reader.TestToggleTheme();
            await Task.Delay(900);
            novelLightThemeOk = reader.NovelBackgroundHex.Length > 0 && !reader.NovelBackgroundHex.Contains("1A1A2E");
            reader.TestToggleTheme();
            await Task.Delay(600);
            Capture(reader, Path.Combine(outDir, "novel-paged.png"));
            reader.Close();

            var novelReader2 = new ReaderWindow(novel) { ShowInTaskbar = false };
            novelReader2.Show();
            await Task.Delay(2200);
            novelRestoreOk = novelReader2.IsNovelReady && novelReader2.NovelChapterIndex >= novelChapterBefore;
            novelReader2.Close();
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
        main.Close();

        string summary =
            $"SMOKE 完成 | 样例目录: {samplesDir}\n" +
            $"导入: 新增 {result.Imported}, 更新 {result.Updated}, 失败 {result.Failed.Count}\n" +
            $"书架: {App.Library.GetBooks().Count} 本 (漫画 {comic is not null}, 小说 {novel is not null})\n" +
            $"漫画翻页: {comicReaderOk} | 快速翻页: {pagingOk} | 条漫: {webtoonOk} | 条漫滚动: {webtoonScrollOk} | 双页: {doubleOk} | 小说: {novelReaderOk} | PDF: {pdfReaderOk}\n" +
            $"默认阅读方式: 条漫={defaultModeOk} 打开即条漫={defaultOpensWebtoon}\n" +
            $"按书记忆: 翻页模式={modePersisted} 缩放150%={zoomPersisted} 比例数字={zoomTextRestored}\n" +
            $"缩放同步: 比例数字={zoomTextSyncOk}\n" +
            $"小说: 翻页进度={novelProgressOk} 浅色背景={novelLightThemeOk} 恢复进度={novelRestoreOk}\n" +
            $"关闭阅读器耗时: {closeSeconds:F1}s\n" +
            (tallPdf.Length > 0 ? $"超长 PDF 条漫切换: {tallWebtoonOk}（耗时 {tallWebtoonSeconds:F1}s，页数 {readerWebtoonPages}）\n" : "") +
            (webtoonOk ? "条漫诊断: " + readerWebtoonStats + "\n" : "") +
            (comicError is null ? "" : "异常: " + comicError + "\n") +
            $"截图目录: {outDir}";

        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary);

        bool tallOk = tallPdf.Length == 0 || tallWebtoonOk;
        bool closeOk = closeSeconds >= 0 && closeSeconds < 3;
        return comic is not null && novel is not null && comicReaderOk && pagingOk && webtoonOk && webtoonScrollOk &&
               doubleOk && novelReaderOk && pdfReaderOk && tallOk && closeOk && defaultModeOk && defaultOpensWebtoon &&
               modePersisted && zoomPersisted && zoomTextSyncOk && zoomTextRestored &&
               novelProgressOk && novelLightThemeOk && novelRestoreOk ? 0 : 1;
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
}
