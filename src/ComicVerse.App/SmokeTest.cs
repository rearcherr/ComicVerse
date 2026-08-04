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
        string? comicError = null;
        string readerWebtoonStats = "";

        if (comic is not null)
        {
            var reader = new ReaderWindow(comic) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1600);
            comicReaderOk = reader.IsComicPageLoaded;
            Capture(reader, Path.Combine(outDir, "reader-paged.png"));
            try
            {
                reader.TestSwitchToWebtoon();
                await Task.Delay(1600);
                webtoonOk = reader.IsWebtoonReady;
                readerWebtoonStats = reader.WebtoonStats;
                Capture(reader, Path.Combine(outDir, "reader-webtoon.png"));
                reader.TestSwitchToDouble();
                await Task.Delay(1400);
                doubleOk = reader.IsDoubleReady;
                Capture(reader, Path.Combine(outDir, "reader-double.png"));
            }
            catch (Exception ex)
            {
                comicError = ex.Message;
            }
            reader.Close();
        }

        if (novel is not null)
        {
            var reader = new ReaderWindow(novel) { ShowInTaskbar = false };
            reader.Show();
            await Task.Delay(1800);
            novelReaderOk = reader.IsNovelReady;
            Capture(reader, Path.Combine(outDir, "novel-paged.png"));
            reader.Close();
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
                pdfReaderOk = reader.IsComicPageLoaded;
                Capture(reader, Path.Combine(outDir, "reader-pdf.png"));
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
            $"漫画翻页: {comicReaderOk} | 条漫: {webtoonOk} | 双页: {doubleOk} | 小说: {novelReaderOk} | PDF: {pdfReaderOk}\n" +
            (webtoonOk ? "条漫诊断: " + readerWebtoonStats + "\n" : "") +
            (comicError is null ? "" : "异常: " + comicError + "\n") +
            $"截图目录: {outDir}";

        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary);

        return comic is not null && novel is not null && comicReaderOk && webtoonOk && doubleOk && novelReaderOk && pdfReaderOk ? 0 : 1;
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
