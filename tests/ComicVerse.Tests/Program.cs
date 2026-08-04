using System.IO;
using System.Text;
using System.Windows.Media;
using ComicVerse.Core;
using ComicVerse.Core.Comics;
using ComicVerse.Core.Models;
using ComicVerse.Core.Novel;
using ComicVerse.Core.Services;

namespace ComicVerse.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Contains("--samples"))
        {
            GenerateSamples();
            return 0;
        }

        if (args.Contains("--pdfinfo") && args.Length > 1)
        {
            DumpPdfInfo(args[1]);
            return 0;
        }

        if (args.Contains("--pdfraw") && args.Length > 1)
        {
            ProbePdfRaw(args[1]);
            return 0;
        }

        if (args.Contains("--pdfslice") && args.Length > 2)
        {
            using var source = new ComicVerse.Core.Comics.PdfComicSource(args[1]);
            using var stream = source.GetPageStream(0);
            var img = ImageHelper.DecodeFrozen(stream);
            File.WriteAllBytes(args[2], ImageHelper.EncodePng(img!));
            Console.WriteLine($"已导出第 1 片: {img!.PixelWidth}x{img.PixelHeight} -> {args[2]}");
            return 0;
        }

        string work = Path.Combine(Path.GetTempPath(), "comicverse-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            var samples = BuildSamples(work);
            Run("自然排序", TestNaturalSort);
            Run("编码检测", () => TestEncodingDetection(work));
            Run("ZIP/CBZ 漫画源", () => TestZipSource(samples.Cbz));
            Run("TAR/CBT 漫画源", () => TestTarSource(samples.Tar));
            Run("文件夹漫画源", () => TestFolderSource(samples.Folder));
            Run("伪 CBR 回退", () => TestFakeCbrFallback(work));
            Run("PDF 漫画源", () => TestPdfSource(samples.Pdf));
            Run("超长条漫 PDF 切片", () => TestTallPdfSlice(samples.TallPdf));
            Run("PDF 远页渲染", () => TestPdfFarPage(samples.MultiPdf));
            Run("长漫画远距离跳页", () => TestFarJumpLoad(work));
            Run("TXT 章节解析", () => TestTxtParse(samples.TxtUtf8));
            Run("EPUB 解析", () => TestEpubParse(samples.Epub));
            Run("指纹稳定", () => TestFingerprint(work, samples.Cbz));
            Run("封面生成", () => TestCoverGeneration(work));
            Run("漫画加载器缓存", () => TestComicLoader(samples.Cbz));
            Run("书库与进度", () => TestLibrary(samples));

            Console.WriteLine();
            Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
            foreach (var f in Failures)
                Console.WriteLine("  ✗ " + f);
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("测试执行异常: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("  ✓ " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine("  ✗ " + name + " — " + ex.Message);
        }
    }

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }

    private static Samples BuildSamples(string work)
    {
        string imgDir = Path.Combine(work, "images");
        Directory.CreateDirectory(imgDir);
        var pages = new List<string>();
        for (int i = 1; i <= 10; i++)
        {
            string p = Path.Combine(imgDir, $"p{i}.png");
            TestData.MakePng(p, i % 2 == 0 ? 800 : 600, 1100, Color.FromRgb((byte)(40 + i * 18), (byte)(80 + i * 10), 200), $"P{i}");
            pages.Add(p);
        }

        string cbz = Path.Combine(work, "sample.cbz");
        TestData.MakeCbz(cbz, pages.Select((p, i) => ($"pages/page{i + 1:00}.png", p)));

        string tar = Path.Combine(work, "sample.cbt");
        TestData.MakeTar(tar, pages.Select((p, i) => ($"p{i + 1:00}.png", p)));

        string folder = Path.Combine(work, "folder-comic");
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= 10; i++)
            File.Copy(pages[i - 1], Path.Combine(folder, $"第{i:00}页.png"));

        string pdf = Path.Combine(work, "sample.pdf");
        TestData.MakeMinimalPdf(pdf);
        string tallPdf = Path.Combine(work, "tall.pdf");
        TestData.MakeMinimalPdf(tallPdf, pageWidth: 100, pageHeight: 10000);
        string multiPdf = Path.Combine(work, "long.pdf");
        TestData.MakeMultiPagePdf(multiPdf, 40);

        string txtUtf8 = Path.Combine(work, "novel-utf8.txt");
        File.WriteAllText(txtUtf8, "第一卷 序章\n\n这是序章的内容，用来测试小说解析。\n\n第二卷 第一章\n\n第一章正文……\n\n第二章 出发\n\n第二章正文。\n", new UTF8Encoding(false));

        string txtGbk = Path.Combine(work, "novel-gbk.txt");
        File.WriteAllText(txtGbk, "序章\n\n这是用GBK编码保存的中文小说内容。\n\n第一章 初见\n\n第一次见面。\n", Encoding.GetEncoding(936));

        string epub = Path.Combine(work, "novel.epub");
        TestData.MakeEpub(epub, "测试轻小说", "测试作者", new List<(string, string)>
        {
            ("第一章 相遇", "<p>这是第一章的内容，主角与伙伴相遇了。</p><p>他们决定一起冒险。</p>"),
            ("第二章 启程", "<p>第二章：众人收拾行囊，踏上旅程。</p>")
        });

        return new Samples(cbz, tar, folder, pdf, txtUtf8, txtGbk, epub, tallPdf, multiPdf);
    }

    private sealed record Samples(string Cbz, string Tar, string Folder, string Pdf, string TxtUtf8, string TxtGbk, string Epub, string TallPdf, string MultiPdf);

    private static void GenerateSamples()
    {
        string samplesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");
        samplesDir = Path.GetFullPath(samplesDir);
        if (Directory.Exists(samplesDir))
            Directory.Delete(samplesDir, true);
        Directory.CreateDirectory(samplesDir);

        string imgDir = Path.Combine(samplesDir, "_gen");
        Directory.CreateDirectory(imgDir);
        var pages = new List<string>();
        for (int i = 1; i <= 10; i++)
        {
            string p = Path.Combine(imgDir, $"p{i}.png");
            TestData.MakePng(p, i % 2 == 0 ? 900 : 650, 1200, Color.FromRgb((byte)(35 + i * 20), (byte)(70 + i * 12), 210), $"第 {i} 页");
            pages.Add(p);
        }

        TestData.MakeCbz(Path.Combine(samplesDir, "示例漫画·星之海.cbz"), pages.Select((p, i) => ($"pages/第{i + 1:00}话.png", p)));
        TestData.MakeTar(Path.Combine(samplesDir, "示例漫画·短篇集.cbt"), pages.Take(4).Select((p, i) => ($"p{i + 1:00}.png", p)));

        string folder = Path.Combine(samplesDir, "示例漫画文件夹·旅行日记");
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= 4; i++)
            File.Copy(pages[i - 1], Path.Combine(folder, $"第{i}页.png"));

        TestData.MakeMinimalPdf(Path.Combine(samplesDir, "示例漫画·PDF.pdf"));

        string utf8 = Path.Combine(samplesDir, "示例轻小说·星海旅人-UTF8.txt");
        File.WriteAllText(utf8, """
            第一卷 序章

            深蓝色的夜空下，少女站在灯塔顶端，望着远方海平线上缓缓亮起的星光。
            「这一次，我要亲自去看看那片星海。」

            第一章 启航

            清晨的港口弥漫着海雾，帆船在码头轻轻晃动。
            少年背着一只旧皮箱，站在船头，等待着未知的旅程。

            第二章 风暴

            第三天夜里，风暴毫无预兆地降临。
            巨浪拍打着船身，少女却笑了——她终于等到了这一刻。

            """, new UTF8Encoding(false));

        string gbk = Path.Combine(samplesDir, "示例轻小说·古风短篇-GBK.txt");
        File.WriteAllText(gbk, "第一章 初见\n\n长安城的春天来得早，柳絮已经飘满了朱雀大街。\n\n第二章 别离\n\n三年后，他在城门外送她远行。\n", Encoding.GetEncoding(936));

        TestData.MakeEpub(Path.Combine(samplesDir, "示例轻小说·电子书.epub"), "星海旅人", "示例作者", new List<(string, string)>
        {
            ("第一章 相遇", "<p>主角在废弃车站遇见了一位自称「星海导航员」的少女。</p><p>她递给他一张泛黄的船票。</p>"),
            ("第二章 启程", "<p>列车在午夜驶向未知的终点，窗外的星空开始流动。</p>")
        });

        Directory.Delete(imgDir, true);
        Console.WriteLine("样例已生成到: " + samplesDir);
        foreach (var f in Directory.GetFileSystemEntries(samplesDir))
            Console.WriteLine("  " + Path.GetFileName(f));
    }

    private static void TestNaturalSort()
    {
        var c = NaturalStringComparer.Instance;
        var list = new List<string> { "page10.png", "page2.png", "page1.png", "page02.png" };
        list.Sort(c);
        Assert(list.SequenceEqual(new[] { "page1.png", "page2.png", "page02.png", "page10.png" }), "自然排序结果错误: " + string.Join(",", list));
    }

    private static void TestEncodingDetection(string work)
    {
        var utf8 = File.ReadAllBytes(Path.Combine(work, "novel-utf8.txt"));
        Assert(EncodingDetector.Detect(utf8).WebName.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase), "UTF-8 未识别");
        var gbk = File.ReadAllBytes(Path.Combine(work, "novel-gbk.txt"));
        var detected = EncodingDetector.Detect(gbk);
        Assert(detected.CodePage == 936, "GBK 未识别，得到 " + detected.CodePage);
        string text = detected.GetString(gbk);
        Assert(text.Contains("第一章"), "GBK 解码出现乱码: " + text[..Math.Min(40, text.Length)]);
    }

    private static void TestZipSource(string path)
    {
        using var source = new ZipComicSource(path);
        Assert(source.PageCount == 10, "ZIP 页数错误: " + source.PageCount);
        using var s0 = source.GetPageStream(0);
        var img = ImageHelper.DecodeFrozen(s0);
        Assert(img is not null && img.PixelWidth > 0, "ZIP 首页解码失败");
        using var s9 = source.GetPageStream(9);
        Assert(ImageHelper.DecodeFrozen(s9) is not null, "ZIP 末页解码失败");
    }

    private static void TestTarSource(string path)
    {
        using var source = new TarComicSource(path);
        Assert(source.PageCount == 10, "TAR 页数错误: " + source.PageCount);
        using var s0 = source.GetPageStream(0);
        Assert(ImageHelper.DecodeFrozen(s0) is not null, "TAR 首页解码失败");
    }

    private static void TestFolderSource(string path)
    {
        using var source = new FolderComicSource(path);
        Assert(source.PageCount == 10, "文件夹页数错误: " + source.PageCount);
        using var s = source.GetPageStream(2);
        Assert(ImageHelper.DecodeFrozen(s) is not null, "文件夹页解码失败");
    }

    private static void TestFakeCbrFallback(string work)
    {
        // 把 zip 改成 .cbr 扩展名，应回退到 ZIP 读取成功
        string fake = Path.Combine(work, "fake.cbr");
        File.Copy(Path.Combine(work, "sample.cbz"), fake);
        using var source = ComicSourceFactory.Create(fake);
        Assert(source.PageCount == 10, "伪 CBR 回退失败");
    }

    private static void TestPdfSource(string path)
    {
        try
        {
            using var source = new PdfComicSource(path);
            Assert(source.PageCount >= 1, "PDF 页数错误");
            using var s0 = source.GetPageStream(0);
            var img = ImageHelper.DecodeFrozen(s0);
            Assert(img is not null && img.PixelWidth > 0, "PDF 渲染解码失败");
        }
        catch (ComicSourceException ex)
        {
            throw new Exception("PDF 打开失败: " + ex.Message);
        }
    }

    private static void TestTallPdfSlice(string path)
    {
        using var source = new PdfComicSource(path);
        // 100 x 10000pt，纵横比 100 → 期望切片数 ceil(100 / 2.2) = 46
        Assert(source.PageCount == 46, "超长页切片数错误: " + source.PageCount);
        var size = source.GetPageSize(0);
        Assert(size is { Width: 1600 }, "切片尺寸快速路径错误: " + size?.Width);
        Assert(size is { Height: >= 64 and <= 4096 }, "切片尺寸高度越界: " + size?.Height);
        using var s0 = source.GetPageStream(0);
        var img = ImageHelper.DecodeFrozen(s0);
        Assert(img is not null && img.PixelWidth == 1600, "超长页切片渲染失败，宽=" + img?.PixelWidth);
        Assert(img!.PixelHeight is >= 64 and <= 4096, "切片高度越界: " + img.PixelHeight);
        Assert(size!.Value.Height == img.PixelHeight, $"快速路径尺寸与渲染尺寸不一致: {size.Value.Height} vs {img.PixelHeight}");
    }

    private static void TestPdfFarPage(string path)
    {
        using var source = new PdfComicSource(path);
        Assert(source.PageCount == 40, "多页 PDF 页数错误: " + source.PageCount);
        using var s = source.GetPageStream(39);
        var img = ImageHelper.DecodeFrozen(s);
        Assert(img is not null && img.PixelWidth == 1600, "PDF 远页渲染失败");
    }

    private static void TestFarJumpLoad(string work)
    {
        // 构造 120 页长漫画，模拟进度条快速拖动后落到末页
        string imgDir = Path.Combine(work, "long-imgs");
        Directory.CreateDirectory(imgDir);
        var pages = new List<string>();
        for (int i = 1; i <= 120; i++)
        {
            string p = Path.Combine(imgDir, $"p{i:000}.png");
            TestData.MakePng(p, 480, 880, Color.FromRgb((byte)(30 + i % 200), (byte)(60 + i % 120), 210), $"P{i}");
            pages.Add(p);
        }
        string cbz = Path.Combine(work, "long.cbz");
        TestData.MakeCbz(cbz, pages.Select((p, i) => ($"pages/p{i + 1:000}.png", p)));

        using var source = new ZipComicSource(cbz);
        var cache = new ImageCacheService { MaxBytes = 256L * 1024 * 1024 };
        using var loader = new ComicImageLoader(source, cache, prefetchCount: 2);
        Assert(loader.PageCount == 120, "长漫画页数错误: " + loader.PageCount);

        // 第一波快速跳页请求随后被用户取消（模拟继续拖到更远）
        using var jumpCts = new CancellationTokenSource();
        var tasks = new List<Task<System.Windows.Media.Imaging.BitmapSource?>>();
        for (int i = 10; i < 90; i += 2)
            tasks.Add(loader.GetPageAsync(i, jumpCts.Token));
        jumpCts.Cancel();
        tasks.Add(loader.GetPageAsync(119));

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
        Assert(results[^1] is not null, "远距离跳页后末页为 null（白屏）");
        Assert(loader.GetCached(119) is not null, "远距离跳页后末页未入缓存");

        // 同一页的请求被取消后，再次请求同一页必须能重新解码，而不是复用已取消的任务
        using (var cancelSame = new CancellationTokenSource())
        {
            var first = loader.GetPageAsync(60, cancelSame.Token);
            cancelSame.Cancel();
            try { first.GetAwaiter().GetResult(); } catch { }
            var again = loader.GetPageAsync(60).GetAwaiter().GetResult();
            Assert(again is not null, "同一页被取消后再请求返回 null（复用了已取消任务）");
        }
    }

    private static void TestTxtParse(string path)
    {
        var enc = EncodingDetector.Detect(File.ReadAllBytes(path));
        var book = TxtParser.Parse(enc.GetString(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path));
        Assert(book.Chapters.Count == 3, "TXT 章节数错误: " + book.Chapters.Count);
        Assert(book.Chapters[0].Blocks.Count >= 2, "TXT 第一章块数错误");
    }

    private static void TestEpubParse(string path)
    {
        var book = new EpubParser().Parse(path);
        Assert(book.Title == "测试轻小说", "EPUB 标题错误: " + book.Title);
        Assert(book.Author == "测试作者", "EPUB 作者错误");
        Assert(book.Chapters.Count == 2, "EPUB 章节数错误: " + book.Chapters.Count);
        bool hasText = book.Chapters[0].Blocks.Any(b => b.Text?.Contains("主角") == true);
        Assert(hasText, "EPUB 正文解析失败");
    }

    private static void TestFingerprint(string work, string cbz)
    {
        string copy = Path.Combine(work, "moved.cbz");
        File.Copy(cbz, copy);
        string f1 = Fingerprint.Compute(cbz);
        string f2 = Fingerprint.Compute(copy);
        Assert(f1 == f2, "文件移动后指纹不一致");
    }

    private static void TestCoverGeneration(string work)
    {
        string dir = Path.Combine(work, "covers");
        Directory.CreateDirectory(dir);
        string img = Path.Combine(work, "cover-src.png");
        TestData.MakePng(img, 1000, 1400, Color.FromRgb(120, 60, 200), "Cover");
        using (var fs = File.OpenRead(img))
        {
            var p1 = CoverGenerator.GenerateFromImage(fs, dir, "c1");
            Assert(p1 is not null && File.Exists(p1), "图片封面未生成");
        }
        var p2 = CoverGenerator.GenerateTextCover("测试书名", "TXT", dir, "c2");
        Assert(p2 is not null && File.Exists(p2), "文字封面未生成");
    }

    private static void TestComicLoader(string cbz)
    {
        using var source = new ZipComicSource(cbz);
        var cache = new ImageCacheService { MaxBytes = 64L * 1024 * 1024 };
        using var loader = new ComicImageLoader(source, cache, prefetchCount: 3);
        Assert(loader.PageCount == 10, "loader 页数错误");
        var page = loader.GetPageAsync(0).GetAwaiter().GetResult();
        Assert(page is not null, "loader 首页为 null");
        Assert(loader.GetCached(0) is not null, "首页未入缓存");
        loader.Prefetch(0);
        // 给预取一点时间
        for (int i = 0; i < 60 && cache.EstimatedBytes == 0; i++)
            Thread.Sleep(50);
        loader.GetPageAsync(3).GetAwaiter().GetResult();
        Assert(loader.GetCached(3) is not null, "第 4 页未入缓存");
    }

    private static void TestLibrary(Samples samples)
    {
        string db = Path.Combine(Path.GetTempPath(), "comicverse-test-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            using var lib = new LibraryService(db);
            var settings = new AppSettingsService(lib);
            Assert(settings.Theme == "dark", "默认主题应为 dark");
            settings.Theme = "light";
            Assert(lib.GetSetting("theme") == "light", "主题保存失败");

            var importer = new ImportService(lib);
            var result = importer.ImportAsync(new[] { samples.Cbz, samples.TxtUtf8, samples.Epub }).GetAwaiter().GetResult();
            Assert(result.Imported == 3 && result.Failed.Count == 0, $"导入失败: imported={result.Imported}, failed={string.Join(";", result.Failed)}");

            var books = lib.GetBooks();
            Assert(books.Count == 3, "书架数量错误: " + books.Count);
            var comic = books.First(b => b.Type == BookType.Comic);
            Assert(comic.PageCount == 10, "漫画页数未入库");
            Assert(comic.CoverPath is not null && File.Exists(comic.CoverPath), "漫画封面未生成");
            var novel = books.First(b => b.Type == BookType.Novel && b.Format == BookFormat.Txt);
            Assert(novel.ChapterCount == 3, "小说章节数未入库: " + novel.ChapterCount);

            lib.SaveProgress(comic.Id, 0.42, 4, 0, 0, "webtoon", 1.5);
            var restored = lib.GetProgress(comic.Id);
            Assert(restored is not null && restored.PageIndex == 4 && restored.Mode == "webtoon"
                && Math.Abs(restored.Zoom - 1.5) < 0.01, "进度恢复失败（含模式/缩放）");
            var recent = lib.GetRecent(10);
            Assert(recent.Count == 1 && recent[0].Id == comic.Id, "最近阅读排序错误");
            var fresh = lib.GetBook(comic.Id);
            Assert(Math.Abs(fresh!.Progress - 0.42) < 0.001, "书籍进度未更新");

            // 重复导入应更新而不是新增
            var again = importer.ImportAsync(new[] { samples.Cbz }).GetAwaiter().GetResult();
            Assert(again.Updated == 1 && lib.GetBooks().Count == 3, "重复导入去重失败");

            lib.AddBookmark(new Bookmark { BookId = comic.Id, PageIndex = 7, Note = "精彩分镜" });
            var bms = lib.GetBookmarks(comic.Id);
            Assert(bms.Count == 1 && bms[0].PageIndex == 7 && bms[0].Note == "精彩分镜", "书签保存失败");
            lib.DeleteBookmark(bms[0].Id);
            Assert(lib.GetBookmarks(comic.Id).Count == 0, "书签删除失败");
        }
        finally
        {
            try { File.Delete(db); } catch { }
            try { File.Delete(db + "-wal"); } catch { }
            try { File.Delete(db + "-shm"); } catch { }
        }
    }

    private static void DumpPdfInfo(string path)
    {
        Console.WriteLine("PDF: " + path);
        using var renderer = new ComicVerse.Core.Comics.PdfNativeRenderer(path);
        Console.WriteLine("PDF 页数: " + renderer.PageCount);
        for (int i = 0; i < Math.Min(renderer.PageCount, 20); i++)
        {
            var s = renderer.GetPageSize(i);
            Console.WriteLine($"  第 {i + 1} 页: {s.Width} x {s.Height} pt");
        }
        using var source = new ComicVerse.Core.Comics.PdfComicSource(path);
        Console.WriteLine("阅读页数（切片后）: " + source.PageCount);
        using var first = source.GetPageStream(0);
        var dim = ImageHelper.GetDimensions(first);
        Console.WriteLine($"  第一页渲染尺寸: {dim?.Width} x {dim?.Height} px");
    }

    private static void ProbePdfRaw(string path)
    {
        PdfRawProbe.FPDF_InitLibrary();
        var doc = PdfRawProbe.FPDF_LoadDocument(System.Text.Encoding.UTF8.GetBytes(path + '\0'), null);
        Console.WriteLine("doc=0x" + doc.ToInt64().ToString("X"));
        int count = PdfRawProbe.FPDF_GetPageCount(doc);
        Console.WriteLine("count=" + count);
        var page = PdfRawProbe.FPDF_LoadPage(doc, 0);
        Console.WriteLine("page=0x" + page.ToInt64().ToString("X"));
        double wF = PdfRawProbe.FPDF_GetPageWidthF(page);
        double hF = PdfRawProbe.FPDF_GetPageHeightF(page);
        double wOld = PdfRawProbe.FPDF_GetPageWidth(page);
        double hOld = PdfRawProbe.FPDF_GetPageHeight(page);
        Console.WriteLine($"widthF={wF} heightF={hF}");
        Console.WriteLine($"widthOld={wOld} heightOld={hOld}");
        PdfRawProbe.FPDF_ClosePage(page);
        PdfRawProbe.FPDF_CloseDocument(doc);
    }

    private static class PdfRawProbe
    {
        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern void FPDF_InitLibrary();

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern IntPtr FPDF_LoadDocument(byte[] pathUtf8, byte[]? password);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int FPDF_GetPageCount(IntPtr doc);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern void FPDF_ClosePage(IntPtr page);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern void FPDF_CloseDocument(IntPtr doc);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern double FPDF_GetPageWidthF(IntPtr page);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern double FPDF_GetPageHeightF(IntPtr page);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern double FPDF_GetPageWidth(IntPtr page);

        [System.Runtime.InteropServices.DllImport("pdfium.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern double FPDF_GetPageHeight(IntPtr page);
    }
}
