using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        if (args.Contains("--pdfscan") && args.Length > 1)
        {
            ScanPdfSlices(args[1]);
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
            Run("跨书缓存隔离", () => TestCacheIsolation(work));
            Run("书库与进度", () => TestLibrary(samples));
            Run("书库并发访问", () => TestLibraryConcurrency(samples));
            Run("取尺寸只读头部", () => TestSizeProbe(work));
            Run("文件夹导入", () => TestFolderImport(work));
            Run("EPUB 正文无标签残留", () => TestEpubBodyHasNoTags(work));
            Run("EPUB href 变体", () => TestEpubHrefVariants(work));
            Run("TXT 前言/空章/目录块", TestTxtChapterEdgeCases);
            Run("伪扩展名魔数嗅探", () => TestFakeExtensionSniffing(work));

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

    /// <summary>共享缓存必须按书隔离：换一本书后同一页号不能命中上一本书的画面。</summary>
    private static void TestCacheIsolation(string work)
    {
        string dirA = Path.Combine(work, "cache-a");
        string dirB = Path.Combine(work, "cache-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var pagesA = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string p = Path.Combine(dirA, $"p{i}.png");
            TestData.MakePng(p, 600, 1100, Color.FromRgb(80, 120, 200), "A" + i);
            pagesA.Add(p);
        }
        var pagesB = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string p = Path.Combine(dirB, $"p{i}.png");
            TestData.MakePng(p, 300, 800, Color.FromRgb(200, 90, 120), "B" + i);
            pagesB.Add(p);
        }

        string cbzA = Path.Combine(work, "cache-a.cbz");
        string cbzB = Path.Combine(work, "cache-b.cbz");
        TestData.MakeCbz(cbzA, pagesA.Select((p, i) => ($"p{i + 1:00}.png", p)));
        TestData.MakeCbz(cbzB, pagesB.Select((p, i) => ($"p{i + 1:00}.png", p)));

        var cache = new ImageCacheService { MaxBytes = 64L * 1024 * 1024 };

        using var srcA = new ZipComicSource(cbzA);
        using var loaderA = new ComicImageLoader(srcA, cache, prefetchCount: 0);
        var a0 = loaderA.GetPageAsync(0).GetAwaiter().GetResult();
        var a1 = loaderA.GetPageAsync(1).GetAwaiter().GetResult();
        Assert(a0 is { PixelWidth: 600, PixelHeight: 1100 }, "A 书第 1 页尺寸异常: " + a0?.PixelWidth + "x" + a0?.PixelHeight);
        Assert(a1 is not null, "A 书第 2 页加载失败");

        using var srcB = new ZipComicSource(cbzB);
        using var loaderB = new ComicImageLoader(srcB, cache, prefetchCount: 0);
        var b0 = loaderB.GetPageAsync(0).GetAwaiter().GetResult();
        Assert(b0 is { PixelWidth: 300, PixelHeight: 800 },
            $"跨书串页：B 书第 1 页拿到了不是它自己的画面（{b0?.PixelWidth}x{b0?.PixelHeight}）");

        // 回到 A 书仍应命中 A 书自己的缓存
        var againA0 = loaderA.GetPageAsync(0).GetAwaiter().GetResult();
        Assert(againA0 is { PixelWidth: 600 }, "A 书缓存被串或丢失");
        Assert(loaderA.GetCached(0) is not null, "A 书第 1 页未命中缓存");
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

    private static void TestLibraryConcurrency(Samples samples)
    {
        string db = Path.Combine(Path.GetTempPath(), "comicverse-conc-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            using var lib = new LibraryService(db);
            var importer = new ImportService(lib);
            var res = importer.ImportAsync(new[] { samples.Cbz, samples.Epub }).GetAwaiter().GetResult();
            Assert(res.Imported == 2, "并发测试准备失败: " + res.Imported);
            long comicId = lib.GetBooks().First(b => b.Type == BookType.Comic).Id;
            long novelId = lib.GetBooks().First(b => b.Type == BookType.Novel).Id;

            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var tasks = new List<Task>();
            for (int t = 0; t < 8; t++)
            {
                int kind = t % 4;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            switch (kind)
                            {
                                case 0:
                                    lib.SaveProgress(comicId, i / 200.0, i % 10, 0, 0, "webtoon", 1.0);
                                    break;
                                case 1:
                                    lib.GetProgress(novelId);
                                    break;
                                case 2:
                                    lib.GetBooks();
                                    break;
                                default:
                                    lib.SetSetting("conc_" + (i % 5), i.ToString());
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.GetType().Name + ": " + ex.Message);
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray());
            Assert(errors.Count == 0, "并发访问书库出错: " + string.Join(" | ", errors.Take(3)));
        }
        finally
        {
            try { File.Delete(db); } catch { }
            try { File.Delete(db + "-wal"); } catch { }
            try { File.Delete(db + "-shm"); } catch { }
        }
    }

    private static void TestSizeProbe(string work)
    {
        // 图片头部已含尺寸信息，取尺寸不应读取整页数据（长漫画排版卡顿的关键）
        string png = Path.Combine(work, "probe.png");
        TestData.MakePng(png, 900, 1400, Color.FromRgb(200, 60, 90), "Probe");
        byte[] bytes = File.ReadAllBytes(png);
        using var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        ms.Write(new byte[4 * 1024 * 1024]); // 模拟“长漫画大页”的尾部数据
        ms.Position = 0;
        var dim = ImageHelper.GetDimensionsFromPrefix(ms, out int read);
        Assert(dim is { Width: 900, Height: 1400 }, "头部尺寸解析错误: " + dim);
        Assert(read <= 384 * 1024, "尺寸探测读取过多数据: " + read);
    }

    private static void TestFolderImport(string work)
    {
        string db = Path.Combine(Path.GetTempPath(), "comicverse-folder-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            using var lib = new LibraryService(db);
            var importer = new ImportService(lib);

            // 夹内多个压缩包：每个压缩包应各自成为一本书，而不是被合并忽略
            string archiveDir = Path.Combine(work, "import-archives");
            Directory.CreateDirectory(archiveDir);
            string vol1 = Path.Combine(archiveDir, "第01卷.cbz");
            string vol2 = Path.Combine(archiveDir, "第02卷.cbz");
            File.Copy(Path.Combine(work, "sample.cbz"), vol1);
            File.Copy(Path.Combine(work, "sample.cbz"), vol2);
            using (var fs = new FileStream(vol2, FileMode.Append, FileAccess.Write))
                fs.Write(new byte[128]); // 让两本指纹不同，避免去重成一本

            var res = importer.ImportAsync(new[] { archiveDir }).GetAwaiter().GetResult();
            Assert(res.Failed.Count == 0, "文件夹导入失败: " + string.Join(";", res.Failed));
            var comics = lib.GetBooks().Where(b => b.Type == BookType.Comic).ToList();
            Assert(comics.Count == 2, "夹内压缩包未各自导入，实际漫画数: " + comics.Count);
            Assert(comics.All(c => c.PageCount == 10), "夹内压缩包页数错误: " + string.Join(",", comics.Select(c => c.PageCount)));

            // 图片文件夹仍然作为一本漫画导入
            string imageDir = Path.Combine(work, "import-images");
            Directory.CreateDirectory(imageDir);
            for (int i = 1; i <= 3; i++)
                TestData.MakePng(Path.Combine(imageDir, $"p{i}.png"), 400, 600, Color.FromRgb((byte)(60 * i), 90, 160), "P" + i);
            importer.ImportAsync(new[] { imageDir }).GetAwaiter().GetResult();
            var folderBook = lib.GetBooks().FirstOrDefault(b => string.Equals(b.FilePath, imageDir, StringComparison.OrdinalIgnoreCase));
            Assert(folderBook is not null && folderBook.PageCount == 3, "图片文件夹未作为一本漫画导入");
        }
        finally
        {
            try { File.Delete(db); } catch { }
            try { File.Delete(db + "-wal"); } catch { }
            try { File.Delete(db + "-shm"); } catch { }
        }
    }

    /// <summary>抽样渲染若干切片并统计非白像素比例，判断渲染结果是否为空白图。</summary>
    private static void ScanPdfSlices(string path)
    {
        using var source = new ComicVerse.Core.Comics.PdfComicSource(path);
        int total = source.PageCount;
        Console.WriteLine($"切片总数: {total}");
        var indexes = new List<int> { 0, 1, total / 20, total / 10, total / 4, total / 2, total * 3 / 4, total - 10, total - 2, total - 1 };
        foreach (int idx in indexes.Distinct().Where(i => i >= 0 && i < total))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var stream = source.GetPageStream(idx);
            long bytes = stream.Length;
            var img = ImageHelper.DecodeFrozen(stream);
            sw.Stop();
            if (img is null)
            {
                Console.WriteLine($"  第 {idx + 1} 片: 解码失败");
                continue;
            }
            var converted = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var buf = new byte[stride * h];
            converted.CopyPixels(buf, stride, 0);
            long nonWhite = 0, sampled = 0;
            for (int i = 0; i + 3 < buf.Length; i += 4 * 37)
            {
                sampled++;
                if (buf[i] < 240 || buf[i + 1] < 240 || buf[i + 2] < 240) nonWhite++;
            }
            Console.WriteLine($"  第 {idx + 1} 片: {w}x{h} PNG={bytes / 1024}KB 非白={nonWhite * 100.0 / Math.Max(1, sampled):F1}% 用时={sw.ElapsedMilliseconds}ms");
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var source = new ComicVerse.Core.Comics.PdfComicSource(path);
        sw.Stop();
        Console.WriteLine($"建立阅读页索引用时: {sw.ElapsedMilliseconds}ms（{source.PageCount} 页）");
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

    // ================= 解析层回归测试（FIX-2） =================

    /// <summary>构造一个 href 可自定义的 EPUB，用于验证 href 解析变体。</summary>
    private static void MakeEpubWithHrefs(string path, string opfDir,
        List<(string Href, string EntryPath, string Html)> chapters)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var container = zip.CreateEntry("META-INF/container.xml");
        using (var s = container.Open())
        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
        {
            w.Write("<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"" +
                    opfDir + "/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        }

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (int i = 0; i < chapters.Count; i++)
        {
            manifest.Append($"<item id=\"c{i}\" href=\"{chapters[i].Href}\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{i}\"/>");
        }

        var opf = zip.CreateEntry(opfDir + "/content.opf");
        using (var s = opf.Open())
        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
        {
            w.Write("<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\">" +
                    "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>HrefTest</dc:title><dc:creator>A</dc:creator></metadata>" +
                    "<manifest>" + manifest + "</manifest><spine>" + spine + "</spine></package>");
        }

        foreach (var (_, entryPath, html) in chapters)
        {
            var xh = zip.CreateEntry(entryPath);
            using var s = xh.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write($"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body><h1>章</h1>{html}</body></html>");
        }
    }

    private static void TestEpubBodyHasNoTags(string work)
    {
        // 正文必须不含任何标签字符；XML 声明与 <head> 内容也不能混进来
        string path = Path.Combine(work, "body-tags.epub");
        TestData.MakeEpub(path, "标签测试", "作者", new List<(string, string)>
        {
            ("第一章", "<p>正文一</p><p>正文二</p>"),
        });

        var book = new EpubParser().Parse(path);
        Assert(book.Chapters.Count == 1, "EPUB 章节数错误: " + book.Chapters.Count);

        var texts = book.Chapters[0].Blocks.Where(b => !b.IsImage && b.Text is not null).Select(b => b.Text!).ToList();
        Assert(texts.Count > 0, "EPUB 正文为空");

        foreach (string t in texts)
        {
            Assert(!t.Contains('<'), "EPUB 正文残留标签字符 '<': \"" + t + "\"");
            Assert(!t.Contains('>'), "EPUB 正文残留标签字符 '>': \"" + t + "\"");
            Assert(!t.Contains("<?xml"), "EPUB 正文混入 XML 声明: \"" + t + "\"");
        }

        Assert(texts.Any(t => t.Contains("正文一")), "EPUB 正文第一段缺失");
        Assert(texts.Any(t => t.Contains("正文二")), "EPUB 正文第二段缺失");
    }

    private static void TestEpubHrefVariants(string work)
    {
        // 5 种合法 href 写法都必须能解析出章节
        var cases = new (string Name, string Href, string Entry)[]
        {
            ("普通相对路径", "c0.xhtml", "OEBPS/c0.xhtml"),
            ("fragment", "c0.xhtml#part1", "OEBPS/c0.xhtml"),
            ("query", "c0.xhtml?v=1", "OEBPS/c0.xhtml"),
            ("百分号编码", "my%20chapter.xhtml", "OEBPS/my chapter.xhtml"),
            ("leading slash", "/ch0.xhtml", "OEBPS/ch0.xhtml"),
            ("父目录 ../", "../text/c0.xhtml", "text/c0.xhtml"),
        };

        foreach (var (name, href, entry) in cases)
        {
            string path = Path.Combine(work, "href-" + Guid.NewGuid().ToString("N")[..6] + ".epub");
            MakeEpubWithHrefs(path, "OEBPS", new List<(string, string, string)>
            {
                (href, entry, "<p>正文内容</p>"),
            });

            NovelBook book;
            try
            {
                book = new EpubParser().Parse(path);
            }
            catch (Exception ex)
            {
                throw new Exception($"EPUB href「{name}」({href}) 解析失败: {ex.Message}");
            }

            Assert(book.Chapters.Count == 1, $"EPUB href「{name}」({href}) 章节数错误: {book.Chapters.Count}");
            Assert(book.Chapters[0].Blocks.Any(b => b.Text is not null && b.Text.Contains("正文内容")),
                $"EPUB href「{name}」({href}) 正文缺失");
        }
    }

    private static void TestTxtChapterEdgeCases()
    {
        // 1) 首个标题之前的前言不能被当成第 0 章，也不能覆盖书名
        var b1 = TxtParser.Parse("书名：测试\n作者：某某\n\n第一章 起始\n内容一", "书");
        Assert(b1.Chapters.Count == 1, "前言被误当成独立章节: " + b1.Chapters.Count);
        Assert(b1.Chapters[0].Title == "第一章 起始", "首章标题被前言覆盖: " + b1.Chapters[0].Title);
        Assert(b1.Chapters[0].Blocks.Any(x => x.Text is not null && x.Text.Contains("书名：测试")),
            "前言内容丢失");

        // 2) 连续两个标题不能产生只有标题的空章
        var b2 = TxtParser.Parse("第一章 起始\n内容一\n\n第二章 结束\n第三章 开始\n内容三", "书");
        Assert(b2.Chapters.Count == 2, "连续标题产生了多余章节: " + b2.Chapters.Count);
        foreach (var c in b2.Chapters)
        {
            Assert(c.Blocks.Any(x => !x.IsHeading), "出现只有标题没有正文的空章: " + c.Title);
        }

        // 3) 文首目录块不应被当成正文，同名首章不能被吞掉
        var b3 = TxtParser.Parse(
            "第一章 起始\n第二章 结束\n第三章 开始\n\n\n第一章 起始\n正文一\n第二章 结束\n正文二", "书");
        Assert(b3.Chapters.Count == 2, "目录块未被识别，章节数=" + b3.Chapters.Count);
        Assert(b3.Chapters[0].Title == "第一章 起始", "目录块处理错误，首章标题=" + b3.Chapters[0].Title);
        Assert(b3.Chapters[0].Blocks.Any(x => x.Text is not null && x.Text.Contains("正文一")),
            "目录块处理错误，首章正文丢失");
        Assert(b3.Chapters[1].Title == "第二章 结束", "目录块处理错误，次章标题=" + b3.Chapters[1].Title);
        foreach (var c in b3.Chapters)
        {
            Assert(c.Blocks.Any(x => !x.IsHeading), "目录块残留了只有标题的空章: " + c.Title);
        }

        // 4) 补全的标题分隔符：：、. 都要能识别
        foreach (string heading in new[] { "第一章：开始", "第一章、开始", "第一章.开始", "第五話 終章", "番外 其一" })
        {
            var b = TxtParser.Parse("正文前\n" + heading + "\n正文后", "书");
            Assert(b.Chapters.Any(c => c.Title == heading || c.Title.Contains(heading)),
                $"标题分隔符未识别: {heading}（章节=" + string.Join("/", b.Chapters.Select(c => c.Title)) + "）");
        }

        // 5) 行中出现的标题词不应误判
        var b5 = TxtParser.Parse("这是第一章 起始 出现在中间的一句话。\n正文", "书");
        Assert(b5.Chapters.Count == 1, "行中标题词被误判为章节");
    }

    private static void TestFakeExtensionSniffing(string work)
    {
        string imgDir = Path.Combine(work, "sniff-imgs");
        Directory.CreateDirectory(imgDir);
        var pages = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string p = Path.Combine(imgDir, $"p{i}.png");
            TestData.MakePng(p, 400, 600, Color.FromRgb((byte)(60 * i), 90, 160), "P" + i);
            pages.Add(p);
        }

        // 真正的 ZIP 内容，用各种"错误"扩展名保存，都应能打开
        string realZip = Path.Combine(work, "sniff-real.zip");
        TestData.MakeCbz(realZip, pages.Select((p, i) => ($"p{i + 1}.png", p)));

        foreach (string ext in new[] { ".cbz", ".cbr", ".rar", ".cbt", ".tar", ".cb7", ".7z", ".zip" })
        {
            string fake = Path.Combine(work, "sniff-zip" + ext);
            File.Copy(realZip, fake, true);
            using var source = ComicSourceFactory.Create(fake);
            Assert(source.PageCount == 3, $"ZIP 内容命名为 {ext} 打开失败，页数={source.PageCount}");
        }

        // 真正的 TAR 内容，命名为 .cbr / .cbz 也应能打开
        string realTar = Path.Combine(work, "sniff-real.tar");
        TestData.MakeTar(realTar, pages.Select((p, i) => ($"p{i + 1}.png", p)));

        foreach (string ext in new[] { ".cbt", ".tar", ".cbr", ".cbz", ".zip" })
        {
            string fake = Path.Combine(work, "sniff-tar" + ext);
            File.Copy(realTar, fake, true);
            using var source = ComicSourceFactory.Create(fake);
            Assert(source.PageCount == 3, $"TAR 内容命名为 {ext} 打开失败，页数={source.PageCount}");
        }

        // 损坏的文件必须给出有意义的错误，而不是被无关格式的 "Rar signature not found" 掩盖
        string garbage = Path.Combine(work, "sniff-garbage.cbr");
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("PK\u0003\u0004this is not a real zip archive"));
        try
        {
            using var source = ComicSourceFactory.Create(garbage);
            Assert(false, "损坏的 .cbr 竟然打开成功，页数=" + source.PageCount);
        }
        catch (ComicSourceException ex)
        {
            Assert(!ex.Message.Contains("未找到图片"), "损坏文件被误报为「未找到图片」: " + ex.Message);
        }
    }
}
