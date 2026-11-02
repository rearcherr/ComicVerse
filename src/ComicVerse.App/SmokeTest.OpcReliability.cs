using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComicVerse.App.Windows;
using ComicVerse.Core.Models;
using ComicVerse.Core.Comics;
using ComicVerse.Core.Services;

namespace ComicVerse.App;

/// <summary>OPC 独立QA：PAX真实导入及坏页失败后的可见内容／持久化一致性。</summary>
public static partial class SmokeTest
{
    private static async Task<(bool Ok, string Detail)> CheckOpcReliabilityAsync(string outDir)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        await OpcPaxCasesAsync(outDir, checks);
        await OpcTarCompatibilityAsync(outDir, checks);
        await OpcPaxBoundsAsync(outDir, checks);
        await OpcPagedFailureAsync(outDir, checks);
        await OpcDoubleFailureAsync(outDir, checks);
        await OpcDoubleRightFailureAsync(outDir, checks);
        await OpcInitialFailureAsync(outDir, checks);
        await OpcControlledRaceAsync(outDir, checks);
        await OpcReturnToFailedViewAsync(outDir, checks);
        await OpcPendingIntentCasesAsync(outDir, checks);
        await OpcWebtoonCompletionEdgesAsync(outDir, checks);
        await File.WriteAllTextAsync(Path.Combine(outDir, "opc-reliability-details.json"),
            JsonSerializer.Serialize(checks.Select(c => new { c.Name, c.Ok, c.Detail }),
                new JsonSerializerOptions { WriteIndented = true }));
        string detail = $"OPC可靠性 {checks.Count(c => c.Ok)}/{checks.Count} — "
            + string.Join(" | ", checks.Select(c => $"{c.Name}={c.Ok}({c.Detail})"));
        return (checks.All(c => c.Ok), detail);
    }

    private static byte[] OpcPng(int marker)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(40 + marker * 18), 105, 185)),
                null, new Rect(0, 0, 240, 600));
        var bitmap = new RenderTargetBitmap(240, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static bool OpcPixels(Image image, int marker)
    {
        if (image.Source is not BitmapSource bitmap || bitmap.PixelWidth != 240 || bitmap.PixelHeight != 600)
            return false;
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(40, 100, 1, 1), pixel, 4, 0);
        return pixel[0] == 185 && pixel[1] == 105 && pixel[2] == 40 + marker * 18;
    }

    private static string OpcDb(Book book)
    {
        var saved = App.Library.GetProgress(book.Id);
        return $"page={saved?.PageIndex};mode={saved?.Mode};progress={App.Library.GetBook(book.Id)?.Progress:F4}";
    }

    private static bool OpcSaved(Book book, int page, string mode, double percent)
    {
        var saved = App.Library.GetProgress(book.Id);
        return saved?.PageIndex == page && saved.Mode == mode
            && Math.Abs((App.Library.GetBook(book.Id)?.Progress ?? -1) - percent) < 0.0001;
    }

    private static async Task<Book> OpcImportAsync(string path)
    {
        var imported = await App.Importer.ImportAsync([path]);
        if (imported.Failed.Count > 0)
            throw new InvalidOperationException(string.Join("; ", imported.Failed));
        return App.Library.GetBookByPath(path) ?? throw new InvalidOperationException("导入后缺书籍: " + path);
    }

    private static string OpcBrokenComic(string outDir, string name, int count, params int[] bad)
    {
        string path = Path.Combine(outDir, "OPC-" + name + ".cbz");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (int page = 0; page < count; page++)
        {
            using var entry = archive.CreateEntry($"p{page:000}.png").Open();
            byte[] bytes = bad.Contains(page) ? Encoding.UTF8.GetBytes("OPC invalid PNG " + name + page) : OpcPng(page);
            entry.Write(bytes);
        }
        using var id = new StreamWriter(archive.CreateEntry("qa-fixture-id.txt").Open());
        id.Write(name);
        return path;
    }

    private static async Task OpcPagedFailureAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "分页坏页", 5, 1, 3));
            App.Library.SaveProgress(book.Id, 0.2, 0, 0, 0, "paged", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool opened = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0));
            checks.Add(("分页正常A基线", opened && reader.PageInfoText.Text == "1 / 5", reader.PageInfoText.Text));
            reader.TestFlushSave();
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool failed = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Visible && reader.LoadingText.Text.Contains("失败"));
            checks.Add(("分页失败保留A内容与页号", failed && OpcPixels(reader.PageImage, 0) && reader.PageInfoText.Text == "1 / 5",
                $"failed={failed};footer={reader.PageInfoText.Text};message={reader.LoadingText.Text}"));
            await Task.Delay(2600);
            checks.Add(("分页坏B未自动推进DB", OpcSaved(book, 0, "paged", 0.2), OpcDb(book)));
            reader.LoadingPanel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            await Task.Delay(350);
            checks.Add(("分页坏B重试仍保留A", reader.LoadingPanel.Visibility == Visibility.Visible
                && reader.LoadingText.Text.Contains("失败") && OpcPixels(reader.PageImage, 0), reader.LoadingText.Text));
            reader.Close();
            checks.Add(("分页失败关闭DB仍A", OpcSaved(book, 0, "paged", 0.2), OpcDb(book)));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool reopened = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0), 3);
            checks.Add(("分页失败重开实际显示A", reopened && reader.PageInfoText.Text == "1 / 5", reader.PageInfoText.Text + ";" + reader.LoadingText.Text));
            reader.TestJumpToPage(0);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool skipped = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2), 3);
            checks.Add(("分页Next从失败B继续C", skipped && reader.PageInfoText.Text == "3 / 5", reader.PageInfoText.Text));
            // 独立明确跳到成功C；不让前一个缺陷令防抖边界没有执行。
            reader.TestJumpToPage(2);
            bool gotC = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            checks.Add(("分页成功C可恢复导航", gotC && reader.PageInfoText.Text == "3 / 5", reader.PageInfoText.Text));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            AddLogicAuditBookmark(reader);
            checks.Add(("分页失败书签绑定成功C", App.Library.GetBookmarks(book.Id).Single().PageIndex == 2,
                "bookmark=" + App.Library.GetBookmarks(book.Id).Single().PageIndex));
            Capture(reader, Path.Combine(outDir, "opc-paged-failure-after-C.png"));
            reader.Close();
            checks.Add(("分页C未过防抖再坏D关闭保存C", gotC && OpcSaved(book, 2, "paged", 0.6), OpcDb(book)));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reopened = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2), 3);
            checks.Add(("分页防抖边界重开实际C", reopened && reader.PageInfoText.Text == "3 / 5", reader.PageInfoText.Text));
            // 故障后显式跳过坏页，完整成功页E仍能成为新进度。
            reader.TestJumpToPage(4);
            bool gotE = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 4));
            reader.TestFlushSave();
            checks.Add(("分页跳过坏页正常完成E", gotE && OpcSaved(book, 4, "paged", 1), OpcDb(book)));
        }
        catch (Exception ex) { checks.Add(("分页坏页组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task OpcDoubleFailureAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "双页坏页", 8, 2, 6));
            App.Library.SaveProgress(book.Id, 0.25, 0, 0, 0, "double", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            bool opened = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1));
            checks.Add(("双页正常0-1基线", opened && reader.PageInfoText.Text == "1-2 / 8", reader.PageInfoText.Text));
            reader.TestFlushSave();
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool failed = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Visible && reader.DoubleLoadingText.Text.Contains("失败"));
            checks.Add(("双页坏2-3不提交半组", failed && OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1)
                && reader.PageInfoText.Text == "1-2 / 8", $"failed={failed};footer={reader.PageInfoText.Text};message={reader.DoubleLoadingText.Text}"));
            await Task.Delay(2600);
            checks.Add(("双页坏组自动保存仍0-1", OpcSaved(book, 0, "double", 0.25), OpcDb(book)));
            reader.DoubleLoading.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            await Task.Delay(350);
            checks.Add(("双页坏组重试不提交", reader.DoubleLoading.Visibility == Visibility.Visible
                && reader.DoubleLoadingText.Text.Contains("失败") && OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1), reader.DoubleLoadingText.Text));
            Capture(reader, Path.Combine(outDir, "opc-double-half-failure.png"));
            reader.Close();
            checks.Add(("双页失败关闭仍存0-1", OpcSaved(book, 0, "double", 0.25), OpcDb(book)));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            bool reopened = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1), 3);
            checks.Add(("双页失败重开显示0-1", reopened && reader.PageInfoText.Text == "1-2 / 8", reader.PageInfoText.Text));
            // 先恢复成功0-1，再以真实Next验证从失败组继续，不依赖私有游标。
            reader.TestSwitchToPaged();
            reader.TestJumpToPage(0);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0));
            reader.TestSwitchToDouble();
            await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed && OpcPixels(reader.LeftImage, 0));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool gotPair = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 4) && OpcPixels(reader.RightImage, 5));
            checks.Add(("双页Next从失败2-3继续4-5", gotPair && reader.PageInfoText.Text == "5-6 / 8", reader.PageInfoText.Text));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Visible && reader.DoubleLoadingText.Text.Contains("失败"));
            AddLogicAuditBookmark(reader);
            checks.Add(("双页失败书签绑定成功4-5", App.Library.GetBookmarks(book.Id).Single().PageIndex == 4,
                "bookmark=" + App.Library.GetBookmarks(book.Id).Single().PageIndex));
            reader.Close();
            checks.Add(("双页4-5未过防抖再坏6-7关闭", gotPair && OpcSaved(book, 4, "double", 0.75), OpcDb(book)));
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            reopened = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 4) && OpcPixels(reader.RightImage, 5), 3);
            checks.Add(("双页防抖边界重开显示4-5", reopened && reader.PageInfoText.Text == "5-6 / 8", reader.PageInfoText.Text));
        }
        catch (Exception ex) { checks.Add(("双页坏页组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    private static async Task OpcDoubleRightFailureAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        bool originalRtl = App.Settings.MangaRightToLeft;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "双页右侧坏页", 6, 3));
            App.Library.SaveProgress(book.Id, 2.0 / 6, 0, 0, 0, "double", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 0) && OpcPixels(reader.RightImage, 1));
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool failed = await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            checks.Add(("双页右侧坏页也原子保留旧组", failed && OpcPixels(reader.LeftImage, 0)
                && OpcPixels(reader.RightImage, 1) && reader.PageInfoText.Text == "1-2 / 6", reader.PageInfoText.Text));
            // LTR失败组2-3，改RTL重绘应仍失败且保持已成功组，而非提交另一半。
            reader.RtlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            reader.Close();
            checks.Add(("双页RTL失败不推进DB", OpcSaved(book, 0, "double", 2.0 / 6), OpcDb(book)));
        }
        catch (Exception ex) { checks.Add(("双页右侧坏页组异常", false, ex.ToString())); }
        finally { reader?.Close(); App.Settings.MangaRightToLeft = originalRtl; }
    }

    private static async Task OpcInitialFailureAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "初始失败历史", 3, 1));
            App.Library.SaveProgress(book.Id, 0.42, 1, 0, 0, "paged", 1.13);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool failed = await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            if (reader.BookmarkButton.IsEnabled) AddLogicAuditBookmark(reader);
            checks.Add(("首次分页无成功画面不建书签", App.Library.GetBookmarks(book.Id).Count == 0,
                $"enabled={reader.BookmarkButton.IsEnabled};count={App.Library.GetBookmarks(book.Id).Count}"));
            reader.Close();
            checks.Add(("首个呈现失败关闭不改历史", failed && OpcSaved(book, 1, "paged", 0.42), OpcDb(book)));
            var doubleBook = await OpcImportAsync(OpcBrokenComic(outDir, "初始双页失败历史", 4, 0));
            App.Library.SaveProgress(doubleBook.Id, 0.18, 0, 0, 0, "double", 1);
            reader = new ReaderWindow(doubleBook) { ShowInTaskbar = false };
            reader.Show();
            failed = await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            bool noHalf = reader.LeftImage.Source is null && reader.RightImage.Source is null;
            if (reader.BookmarkButton.IsEnabled) AddLogicAuditBookmark(reader);
            checks.Add(("首次双页无成功画面不建书签", App.Library.GetBookmarks(doubleBook.Id).Count == 0,
                $"enabled={reader.BookmarkButton.IsEnabled};count={App.Library.GetBookmarks(doubleBook.Id).Count}"));
            reader.Close();
            checks.Add(("首次双页失败不显示半组并保留历史", failed && noHalf && OpcSaved(doubleBook, 0, "double", 0.18),
                $"noHalf={noHalf};" + OpcDb(doubleBook)));
        }
        catch (Exception ex) { checks.Add(("初始失败组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }

    // 写合规PAX extended header，普通header故意采用无扩展占位名。
    // .NET标准TarReader独立核验有效路径/数据，避免把坏fixture误报成产品失败。
    private static void OpcTarMember(Stream stream, string headerName, char type, byte[] bytes, long? declaredSize = null, string? prefix = null)
    {
        var header = new byte[512];
        static void Put(byte[] target, int start, int length, string value)
        {
            byte[] raw = Encoding.UTF8.GetBytes(value);
            Array.Copy(raw, 0, target, start, Math.Min(length, raw.Length));
        }
        Put(header, 0, 100, headerName);
        Put(header, 100, 8, "0000644\0");
        Put(header, 108, 8, "0000000\0");
        Put(header, 116, 8, "0000000\0");
        Put(header, 124, 12, Convert.ToString(declaredSize ?? bytes.Length, 8).PadLeft(11, '0') + "\0");
        Put(header, 136, 12, "00000000000\0");
        for (int i = 148; i < 156; i++) header[i] = 32;
        header[156] = (byte)type;
        Put(header, 257, 6, "ustar\0");
        Put(header, 263, 2, "00");
        if (prefix is not null) Put(header, 345, 155, prefix);
        Put(header, 148, 8, Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ");
        stream.Write(header);
        stream.Write(bytes);
        int padding = (512 - bytes.Length % 512) % 512;
        if (padding > 0) stream.Write(new byte[padding]);
    }

    private static byte[] OpcPaxRecord(string key, string value)
    {
        string tail = " " + key + "=" + value + "\n";
        int size = Encoding.UTF8.GetByteCount(tail) + 1;
        while (true)
        {
            int actual = Encoding.UTF8.GetByteCount(tail) + size.ToString(CultureInfo.InvariantCulture).Length;
            if (actual == size) break;
            size = actual;
        }
        return Encoding.UTF8.GetBytes(size.ToString(CultureInfo.InvariantCulture) + tail);
    }

    private static async Task OpcPaxCasesAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        string prefix = new string('a', 110) + "/中文目录/";
        foreach (bool mixed in new[] { false, true })
        {
            ReaderWindow? reader = null;
            string name = mixed ? "PAX混合条目" : "PAX仅长路径";
            try
            {
                string path = Path.Combine(outDir, "OPC-" + name + ".cbt");
                using (var output = File.Create(path))
                {
                    // 10在2之前写入，读取顺序必须按有效长路径自然排序。
                    OpcTarMember(output, "pax-header-10", 'x', OpcPaxRecord("path", prefix + "p10.png"));
                    OpcTarMember(output, "placeholder-ten", '0', OpcPng(2));
                    OpcTarMember(output, "pax-header-2", 'x', OpcPaxRecord("path", prefix + "p2.png"));
                    OpcTarMember(output, "placeholder-two", '0', OpcPng(1));
                    if (mixed)
                    {
                        OpcTarMember(output, "zz.png", '0', OpcPng(3));
                        OpcTarMember(output, "ignored.txt", '0', Encoding.UTF8.GetBytes(name));
                        OpcTarMember(output, "directory/", '5', []);
                    }
                    output.Write(new byte[1024]);
                }
                var standard = new List<(string Name, long Length)>();
                using (var input = File.OpenRead(path))
                using (var tar = new TarReader(input))
                    while (tar.GetNextEntry() is { } entry)
                        if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                            standard.Add((entry.Name, entry.Length));
                bool fixtureValid = standard.Any(e => e.Name == prefix + "p10.png" && e.Length == OpcPng(2).Length)
                    && standard.Any(e => e.Name == prefix + "p2.png" && e.Length == OpcPng(1).Length);
                checks.Add((name + "标准TarReader校验", fixtureValid, string.Join(";", standard.Select(e => e.Name))));
                if (!fixtureValid) throw new InvalidOperationException("PAX fixture标准读取校验失败");
                var book = await OpcImportAsync(path);
                int expected = mixed ? 3 : 2;
                checks.Add((name + "完整导入页数", book.PageCount == expected, $"actual={book.PageCount};expected={expected}"));
                App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "paged", 1);
                reader = new ReaderWindow(book) { ShowInTaskbar = false };
                reader.Show();
                bool first = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 1));
                checks.Add((name + "自然排序首图p2", first && reader.PageInfoText.Text == $"1 / {expected}", reader.PageInfoText.Text));
                reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool second = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2), 3);
                checks.Add((name + "自然排序次图p10", second && reader.PageInfoText.Text == $"2 / {expected}", reader.PageInfoText.Text));
                if (mixed)
                {
                    reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    bool third = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3), 3);
                    checks.Add((name + "短条目未继承PAX路径", third && reader.PageInfoText.Text == "3 / 3", reader.PageInfoText.Text));
                }
                Capture(reader, Path.Combine(outDir, "opc-" + name + ".png"));
            }
            catch (Exception ex) { checks.Add((name + "真实导入阅读异常", false, ex.ToString())); }
            finally { reader?.Close(); }
        }
    }

    private static async Task OpcTarCompatibilityAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        foreach (string kind in new[] { "USTAR前缀", "GNU长名", "PAX全局局部size" })
        {
            ReaderWindow? reader = null;
            try
            {
                string path = Path.Combine(outDir, "OPC-" + kind + ".cbt");
                string folder = new string('u', 105) + "/中文/";
                using (var output = File.Create(path))
                {
                    if (kind == "PAX全局局部size")
                        OpcTarMember(output, "global", 'g', OpcPaxRecord("comment", "OPC global metadata"));
                    foreach (int page in new[] { 10, 2 })
                    {
                        byte[] image = OpcPng(page == 2 ? 1 : 2);
                        if (kind == "USTAR前缀")
                            OpcTarMember(output, $"p{page}.png", '0', image, prefix: new string('u', 105));
                        else if (kind == "GNU长名")
                        {
                            OpcTarMember(output, "././@LongLink", 'L', Encoding.UTF8.GetBytes(folder + $"p{page}.png\0"));
                            OpcTarMember(output, "placeholder", '0', image);
                        }
                        else
                        {
                            byte[] metadata = OpcPaxRecord("path", folder + $"p{page}.png")
                                .Concat(OpcPaxRecord("size", image.Length.ToString(CultureInfo.InvariantCulture))).ToArray();
                            OpcTarMember(output, "local", 'x', metadata);
                            OpcTarMember(output, "placeholder", '0', image, declaredSize: 0);
                        }
                    }
                    OpcTarMember(output, "zz.png", '0', OpcPng(3));
                    output.Write(new byte[1024]);
                }
                var names = new List<string>();
                using (var input = File.OpenRead(path))
                using (var standard = new TarReader(input))
                    while (standard.GetNextEntry() is { } entry)
                        if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                            names.Add(entry.Name);
                bool fixtureValid = names.Count == 3 && names[0].EndsWith("p10.png") && names[1].EndsWith("p2.png") && names[2] == "zz.png";
                checks.Add((kind + "标准读取fixture", fixtureValid, string.Join(";", names)));
                if (!fixtureValid) throw new InvalidOperationException("compatibility fixture invalid");
                var book = await OpcImportAsync(path);
                App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "paged", 1);
                reader = new ReaderWindow(book) { ShowInTaskbar = false };
                reader.Show();
                bool first = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 1));
                reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool second = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
                reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool third = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3));
                bool random = true;
                foreach (int target in new[] { 1, 0, 2, 1, 2 })
                {
                    reader.TestJumpToPage(target);
                    random &= await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed
                        && OpcPixels(reader.PageImage, target + 1) && reader.PageInfoText.Text == $"{target + 1} / 3");
                }
                checks.Add((kind + "实际App随机重复跳页", random, "order=1,0,2,1,2;pixels/footer=" + random));
                reader.Close();
                checks.Add((kind + "实际App完整排序及保存", book.PageCount == 3 && first && second && third
                    && OpcSaved(book, 2, "paged", 1), $"pages={book.PageCount};pixels={first}/{second}/{third};" + OpcDb(book)));
                reader = new ReaderWindow(book) { ShowInTaskbar = false };
                reader.Show();
                bool reopened = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3));
                checks.Add((kind + "重开页内容", reopened && reader.PageInfoText.Text == "3 / 3", reader.PageInfoText.Text));
            }
            catch (Exception ex) { checks.Add((kind + "组异常", false, ex.ToString())); }
            finally { reader?.Close(); }
        }
    }

    private static async Task OpcPaxBoundsAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        foreach (string kind in new[] { "非法记录长度", "截断元数据", "超1MiB元数据", "非法UTF8" })
        {
            try
            {
                string path = Path.Combine(outDir, "OPC-PAX-拒绝-" + kind + ".cbt");
                using (var output = File.Create(path))
                {
                    byte[] payload = kind switch
                    {
                        "非法记录长度" => Encoding.UTF8.GetBytes("999999 path=bad.png\n"),
                        "非法UTF8" => new byte[] { 49, 52, 32, 112, 97, 116, 104, 61, 255, 46, 112, 110, 103, 10 },
                        _ => OpcPaxRecord("path", "bad.png")
                    };
                    long? declared = kind switch { "截断元数据" => 2048, "超1MiB元数据" => 1048577, _ => null };
                    OpcTarMember(output, "extended", 'x', payload, declared);
                    if (declared is null)
                    {
                        OpcTarMember(output, "placeholder", '0', OpcPng(4));
                        output.Write(new byte[1024]);
                    }
                }
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var imported = await App.Importer.ImportAsync([path]);
                bool safe = imported.Failed.Count == 1 && App.Library.GetBookByPath(path) is null;
                if (kind == "非法UTF8" && imported.Failed.Count == 0 && App.Library.GetBookByPath(path) is { } accepted)
                {
                    // PRD允许标准库安全处理或拒绝；若标准reader替换非法UTF8，必须真实显示完整图片。
                    using var standardFile = File.OpenRead(path);
                    using var standardReader = new TarReader(standardFile);
                    var standardEntry = standardReader.GetNextEntry();
                    App.Library.SaveProgress(accepted.Id, 0, 0, 0, 0, "paged", 1);
                    var reader = new ReaderWindow(accepted) { ShowInTaskbar = false };
                    try
                    {
                        reader.Show();
                        safe = standardEntry?.Name.EndsWith(".png") == true && accepted.PageCount == 1
                            && await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 4), 2);
                    }
                    finally { reader.Close(); }
                }
                checks.Add(("PAX安全处理" + kind, safe
                    && timer.Elapsed.TotalSeconds < 5,
                    $"failed={imported.Failed.Count};seconds={timer.Elapsed.TotalSeconds:F2};" + string.Join(";", imported.Failed)));
            }
            catch (Exception ex) { checks.Add(("PAX异常输入未处理" + kind, false, ex.ToString())); }
        }
    }

    /// <summary>仅QA故障注入：包装真实图片源，延迟有3秒硬上限，不改变生产代码或用户文件。</summary>
    private sealed class OpcControlledSource : IComicSource
    {
        private readonly IComicSource _inner;
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _secondRelease = new(false);
        public volatile int DelayedPage = -1;
        public volatile int SecondDelayedPage = -1;
        public volatile int FailedPage = -1;
        public volatile bool Entered;
        public volatile bool SecondEntered;
        public string SourcePath => _inner.SourcePath + ":opc-controlled";
        public int PageCount => _inner.PageCount;
        public OpcControlledSource(string path) => _inner = ComicSourceFactory.Create(path);
        public Stream GetPageStream(int index)
        {
            if (index == DelayedPage)
            {
                Entered = true;
                if (!_release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("QA bounded page gate elapsed");
            }
            if (index == SecondDelayedPage)
            {
                SecondEntered = true;
                if (!_secondRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("QA bounded secondary page gate elapsed");
            }
            if (index == FailedPage) return new MemoryStream(Encoding.UTF8.GetBytes("QA transient invalid PNG"));
            return _inner.GetPageStream(index);
        }
        public (int Width, int Height)? GetPageSize(int index) => _inner.GetPageSize(index);
        public void ReleasePrimary() { try { _release.Set(); } catch (ObjectDisposedException) { } }
        public void Release() { ReleasePrimary(); try { _secondRelease.Set(); } catch (ObjectDisposedException) { } }
        public void Dispose() { Release(); _inner.Dispose(); _release.Dispose(); _secondRelease.Dispose(); }
    }

    private static OpcControlledSource OpcInstallControlled(ReaderWindow reader, Book book)
    {
        var field = typeof(ReaderWindow).GetField("_loader", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Reader loader field unavailable for QA injection");
        (field.GetValue(reader) as ComicImageLoader)?.Dispose();
        var source = new OpcControlledSource(book.FilePath);
        var loader = new ComicImageLoader(source, new ImageCacheService(), prefetchCount: 0);
        field.SetValue(reader, loader);
        // 已初始化的真实WebtoonView也持有同一loader；只换Reader会留下被释放的测试引用。
        // 保留既有layout/历史位置，仅同步数据源依赖，避免旧fraction场景被夹具本身污染。
        var webtoonLoader = reader.WebtoonView.GetType().GetField("_loader", BindingFlags.Instance | BindingFlags.NonPublic);
        if (webtoonLoader?.GetValue(reader.WebtoonView) is ComicImageLoader)
            webtoonLoader.SetValue(reader.WebtoonView, loader);
        return source;
    }

    private static async Task OpcControlledRaceAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        OpcControlledSource? source = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "有界延迟瞬时故障", 6));
            App.Library.SaveProgress(book.Id, 0.5, 2, 0, 0, "paged", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            source = OpcInstallControlled(reader, book);
            source.DelayedPage = 4;
            reader.TestJumpToPage(4);
            bool entered = await WaitLogicAuditAsync(() => source.Entered, 2);
            reader.TestJumpToPage(3);
            bool latest = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3), 2);
            source.Release();
            await Task.Delay(200);
            reader.TestFlushSave();
            checks.Add(("有界慢请求取消后新成功页胜出", entered && latest && OpcPixels(reader.PageImage, 3)
                && reader.PageInfoText.Text == "4 / 6" && OpcSaved(book, 3, "paged", 4.0 / 6), OpcDb(book)));

            source.Release();
            source = OpcInstallControlled(reader, book);
            source.DelayedPage = 4;
            reader.TestJumpToPage(2);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            reader.TestFlushSave();
            bool success2Saved = OpcSaved(book, 2, "paged", 0.5);
            reader.TestJumpToPage(4);
            entered = await WaitLogicAuditAsync(() => source.Entered, 2);
            AddLogicAuditBookmark(reader);
            bool pendingBookmark2 = App.Library.GetBookmarks(book.Id).Count == 1
                && App.Library.GetBookmarks(book.Id).Single().PageIndex == 2;
            reader.TestSwitchToDouble();
            reader.TestFlushSave();
            bool pendingSafe2 = OpcSaved(book, 2, "paged", 0.5);
            source.Release();
            bool changed = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 4) && OpcPixels(reader.RightImage, 5));
            await Task.Delay(200);
            reader.TestFlushSave();
            // CR-CV-20261103-02-01：有效pending4继承到新模式，替代旧“回2”的矛盾合同。
            // 成功前的持久化/书签仍必须是2；成功后才允许提交完整4-5，不修改failed目标规则。
            checks.Add(("CR-02-01有效pending4跨双页且成功前保留2", success2Saved && entered && pendingBookmark2 && pendingSafe2
                && changed && OpcPixels(reader.LeftImage, 4) && OpcPixels(reader.RightImage, 5)
                && reader.PageInfoText.Text == "5-6 / 6" && OpcSaved(book, 4, "double", 1),
                $"success2={success2Saved};gate={entered};bookmark2={pendingBookmark2};pendingSave2={pendingSafe2};complete45={changed};left4={OpcPixels(reader.LeftImage, 4)};right5={OpcPixels(reader.RightImage, 5)};footer={reader.PageInfoText.Text};" + OpcDb(book)));
            Capture(reader, Path.Combine(outDir, "opc-pending4-double-complete.png"));

            reader.TestSwitchToPaged();
            reader.TestJumpToPage(2); // 显式建立下一条独立场景的成功2，不依赖前一目标4。
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            source.Release();
            source = OpcInstallControlled(reader, book);
            source.DelayedPage = 4;
            reader.TestJumpToPage(3);
            latest = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3));
            reader.TestJumpToPage(4);
            entered = await WaitLogicAuditAsync(() => source.Entered, 2);
            reader.Close();
            source.Release();
            checks.Add(("新成功3后慢4未完成关闭保存3", latest && entered && OpcSaved(book, 3, "paged", 4.0 / 6), OpcDb(book)));

            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 3));
            source = OpcInstallControlled(reader, book);
            source.FailedPage = 4;
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool failed = await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            source.FailedPage = -1;
            reader.LoadingPanel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            bool retried = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 4));
            reader.TestFlushSave();
            checks.Add(("瞬时单页故障重试真实目标恢复", failed && retried && reader.PageInfoText.Text == "5 / 6"
                && OpcSaved(book, 4, "paged", 5.0 / 6), OpcDb(book)));

            reader.TestJumpToPage(0);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0));
            reader.TestSetRtl(false);
            reader.TestSwitchToDouble();
            await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed && OpcPixels(reader.LeftImage, 0));
            source.FailedPage = 3;
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            failed = await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            source.FailedPage = -1;
            reader.DoubleLoading.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            retried = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 2) && OpcPixels(reader.RightImage, 3));
            reader.TestFlushSave();
            checks.Add(("瞬时双页故障重试完整目标组恢复", failed && retried && reader.PageInfoText.Text == "3-4 / 6"
                && OpcSaved(book, 2, "double", 4.0 / 6), OpcDb(book)));
            reader.TestSwitchToPaged();
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            source.FailedPage = 4;
            // 使用新独立cache，避免上一步成功重试的4已在cache中而未触发故障。
            source.Release();
            source = OpcInstallControlled(reader, book);
            source.FailedPage = 4;
            reader.TestJumpToPage(4);
            failed = await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            reader.TestSwitchToDouble();
            changed = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 2) && OpcPixels(reader.RightImage, 3));
            reader.TestFlushSave();
            checks.Add(("已失败目标切模式仍从成功2进入", failed && changed && reader.PageInfoText.Text == "3-4 / 6"
                && OpcSaved(book, 2, "double", 4.0 / 6), OpcDb(book)));
        }
        catch (Exception ex) { checks.Add(("有界延迟瞬时故障组异常", false, ex.ToString())); }
        finally { source?.Release(); reader?.Close(); }
    }

    private static async Task OpcReturnToFailedViewAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        OpcControlledSource? source = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "返回旧成功视图再失败", 6));
            App.Library.SaveProgress(book.Id, 0.5, 2, 0, 0, "paged", 1);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            source = OpcInstallControlled(reader, book);
            source.FailedPage = 3;
            reader.TestSwitchToDouble();
            bool outwardFailure = await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            // 上一失败双页的左图2已成功解码并缓存；换独立cache才确保返paged请求2真的失败。
            source.Release();
            source = OpcInstallControlled(reader, book);
            source.FailedPage = 2;
            reader.TestSwitchToPaged();
            bool returnedFailure = await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            reader.TestFlushSave();
            checks.Add(("分页成功切出未成功再返回失败仍有原图", outwardFailure && returnedFailure
                && OpcPixels(reader.PageImage, 2) && reader.PageInfoText.Text == "3 / 6" && OpcSaved(book, 2, "paged", 0.5),
                $"outFail={outwardFailure};returnFail={returnedFailure};pixels2={OpcPixels(reader.PageImage, 2)};footer={reader.PageInfoText.Text};" + OpcDb(book)));
            Capture(reader, Path.Combine(outDir, "opc-return-paged-failure-retains.png"));

            source.FailedPage = -1;
            reader.TestSwitchToDouble();
            bool successfulPair = await WaitLogicAuditAsync(() => reader.DoubleLoading.Visibility == Visibility.Collapsed
                && OpcPixels(reader.LeftImage, 2) && OpcPixels(reader.RightImage, 3));
            source.Release();
            source = OpcInstallControlled(reader, book);
            source.FailedPage = 2;
            reader.TestSwitchToPaged();
            outwardFailure = await WaitLogicAuditAsync(() => reader.LoadingText.Text.Contains("失败"));
            reader.TestSwitchToDouble();
            returnedFailure = await WaitLogicAuditAsync(() => reader.DoubleLoadingText.Text.Contains("失败"));
            reader.TestFlushSave();
            checks.Add(("双页成功切出未成功再返回失败仍有完整组", successfulPair && outwardFailure && returnedFailure
                && OpcPixels(reader.LeftImage, 2) && OpcPixels(reader.RightImage, 3)
                && reader.PageInfoText.Text == "3-4 / 6" && OpcSaved(book, 2, "double", 4.0 / 6),
                $"pair={successfulPair};outFail={outwardFailure};returnFail={returnedFailure};left2={OpcPixels(reader.LeftImage, 2)};right3={OpcPixels(reader.RightImage, 3)};footer={reader.PageInfoText.Text};" + OpcDb(book)));
            Capture(reader, Path.Combine(outDir, "opc-return-double-failure-retains.png"));
        }
        catch (Exception ex) { checks.Add(("返回成功视图故障组异常", false, ex.ToString())); }
        finally { source?.Release(); reader?.Close(); }
    }

    private static async Task OpcPendingIntentCasesAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        foreach (int target in new[] { 8, 9 })
        {
            ReaderWindow? reader = null;
            OpcControlledSource? source = null;
            try
            {
                var book = await OpcImportAsync(OpcBrokenComic(outDir, "有效pending跨模式" + target, 12));
                int start = target == 9 ? 2 : 0;
                App.Library.SaveProgress(book.Id, target == 9 ? 0.2 : 1.0 / 12, start,
                    target == 9 ? 0.2 : 0, 0, target == 9 ? "webtoon" : "paged", 1);
                reader = new ReaderWindow(book) { ShowInTaskbar = false };
                reader.Show();
                reader.TestSetRtl(false);
                if (target == 9)
                {
                    bool restored = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 2);
                    checks.Add(("pending9旧条漫fraction真实恢复基线", restored, $"page={reader.WebtoonCurrentPage};fraction={reader.WebtoonView.ScrollFraction:F3}"));
                    reader.TestSwitchToPaged();
                }
                bool baseline = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, start));
                reader.TestFlushSave();
                source = OpcInstallControlled(reader, book);
                source.DelayedPage = target;
                reader.TestJumpToPage(target);
                bool entered = await WaitLogicAuditAsync(() => source.Entered, 2);
                if (reader.BookmarkButton.IsEnabled) AddLogicAuditBookmark(reader);
                bool bookmarkSafe = App.Library.GetBookmarks(book.Id).All(b => b.PageIndex == start);
                reader.TestSwitchToDouble();
                reader.TestSwitchToWebtoon();
                reader.TestFlushSave();
                bool pendingSaved = OpcSaved(book, start, "paged", (start + 1.0) / 12);
                checks.Add(($"pending{target}切mode未呈现前保存书签仍成功A", baseline && entered && pendingSaved && bookmarkSafe,
                    $"base={baseline};gate={entered};bookmarkSafe={bookmarkSafe};" + OpcDb(book)));
                source.Release();
                bool final = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == target
                    && reader.WebtoonRenderedLoadedCount > 0 && reader.WebtoonBlankStripCount == 0);
                await Task.Delay(200);
                reader.TestFlushSave();
                var saved = App.Library.GetProgress(book.Id);
                checks.Add(($"pending{target}跨未完成double到webtoon保留显式目标", final
                    && reader.WebtoonVisibleRange.First <= target && reader.WebtoonVisibleRange.Last >= target
                    && reader.PageInfoText.Text == $"{target + 1} / 12" && saved is { Mode: "webtoon" } && saved.PageIndex == target,
                    $"actual={reader.WebtoonCurrentPage};range={reader.WebtoonVisibleRange};footer={reader.PageInfoText.Text};" + OpcDb(book)));
                Capture(reader, Path.Combine(outDir, $"opc-pending-{target}-final.png"));
            }
            catch (Exception ex) { checks.Add(($"pending{target}组异常", false, ex.ToString())); }
            finally { source?.Release(); reader?.Close(); }
        }

        foreach (bool lateFailure in new[] { false, true })
        {
            ReaderWindow? reader = null;
            OpcControlledSource? source = null;
            try
            {
                string label = lateFailure ? "旧失败晚到" : "旧成功晚到";
                var book = await OpcImportAsync(OpcBrokenComic(outDir, label, 12));
                App.Library.SaveProgress(book.Id, 1.0 / 12, 0, 0, 0, "paged", 1);
                reader = new ReaderWindow(book) { ShowInTaskbar = false };
                reader.Show();
                reader.TestSetRtl(false);
                await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 0));
                source = OpcInstallControlled(reader, book);
                source.DelayedPage = 8;
                source.SecondDelayedPage = 6;
                source.FailedPage = lateFailure ? 8 : -1;
                reader.TestJumpToPage(8);
                bool firstGate = await WaitLogicAuditAsync(() => source.Entered, 2);
                reader.TestJumpToPage(6);
                bool secondGate = await WaitLogicAuditAsync(() => source.SecondEntered, 2);
                source.ReleasePrimary();
                // B已结束、C仍被第二个gate阻塞。旧成功或旧失败都不能撤销C的跨模式意图。
                await Task.Delay(200);
                reader.TestSwitchToDouble();
                reader.TestSwitchToWebtoon();
                reader.TestFlushSave();
                bool safe = OpcSaved(book, 0, "paged", 1.0 / 12);
                source.Release();
                bool latest = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 6
                    && reader.WebtoonRenderedLoadedCount > 0 && reader.WebtoonBlankStripCount == 0);
                await Task.Delay(200);
                reader.TestFlushSave();
                var saved = App.Library.GetProgress(book.Id);
                checks.Add((label + "不清更新且仍pending的显式C", firstGate && secondGate && safe && latest
                    && reader.WebtoonCurrentPage == 6 && saved is { Mode: "webtoon", PageIndex: 6 },
                    $"Bgate={firstGate};Cgate={secondGate};safeBeforeCommit={safe};actual={reader.WebtoonCurrentPage};" + OpcDb(book)));
                Capture(reader, Path.Combine(outDir, "opc-" + label + ".png"));
            }
            catch (Exception ex) { checks.Add(($"晚到{lateFailure}组异常", false, ex.ToString())); }
            finally { source?.Release(); reader?.Close(); }
        }

        ReaderWindow? failedReader = null;
        OpcControlledSource? failedSource = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "无成功快照历史pending关闭", 6, 2));
            App.Library.SaveProgress(book.Id, 0.42, 2, 0, 0, "paged", 1.13);
            failedReader = new ReaderWindow(book) { ShowInTaskbar = false };
            failedReader.Show();
            bool failed = await WaitLogicAuditAsync(() => failedReader.LoadingText.Text.Contains("失败"));
            failedSource = OpcInstallControlled(failedReader, book);
            failedSource.DelayedPage = 2;
            failedReader.LoadingPanel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            bool gate = await WaitLogicAuditAsync(() => failedSource.Entered, 2);
            failedReader.TestSwitchToDouble();
            failedReader.Close();
            failedSource.Release();
            checks.Add(("无成功画面历史在重试pending跨模式关闭不改写", failed && gate && OpcSaved(book, 2, "paged", 0.42)
                && Math.Abs((App.Library.GetProgress(book.Id)?.Zoom ?? 0) - 1.13) < 0.001,
                $"initialFailed={failed};retryGate={gate};" + OpcDb(book)));
        }
        catch (Exception ex) { checks.Add(("无成功历史pending组异常", false, ex.ToString())); }
        finally { failedSource?.Release(); failedReader?.Close(); }
    }

    private static async Task OpcWebtoonCompletionEdgesAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        ReaderWindow? reader = null;
        try
        {
            var book = await OpcImportAsync(OpcBrokenComic(outDir, "真实offset完成边界", 12));
            // 条漫zoom是相对适宽倍率；0.2让末页实际高度小于视口，真正触发底部clamp。
            App.Library.SaveProgress(book.Id, 0, 0, 0, 0, "webtoon", 0.2);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            reader.TestSetRtl(false);
            bool initial = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 0
                && reader.WebtoonRenderedLoadedCount > 0 && reader.WebtoonBlankStripCount == 0);
            var scroll = reader.WebtoonView.FindName("Scroll") as ScrollViewer
                ?? throw new InvalidOperationException("真实条漫ScrollViewer不可见");
            double pageHeight = 600 * reader.WebtoonView.CanvasWidth / 240;
            bool nearBuffered = reader.WebtoonVisibleRange.First <= 1 && reader.WebtoonVisibleRange.Last >= 1;
            reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double nearTarget = Math.Min(pageHeight, scroll.ScrollableHeight);
            bool near = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 1
                && Math.Abs(scroll.VerticalOffset - nearTarget) <= 1 && reader.WebtoonBlankStripCount == 0);
            reader.TestFlushSave();
            var saved = App.Library.GetProgress(book.Id);
            checks.Add(("buffer近邻1须真实offset到达后恢复保存", initial && nearBuffered && near
                && reader.PageInfoText.Text == "2 / 12" && saved is { Mode: "webtoon", PageIndex: 1 }
                && Math.Abs(saved.ScrollOffset - reader.WebtoonView.ScrollFraction) < 0.002,
                $"buffered={nearBuffered};offset={scroll.VerticalOffset:F2};target={nearTarget:F2};" + OpcDb(book)));

            // 真实Next命令连续推进到原始页11，检验最后一页的底部clamp，不能以buffer当作到达。
            for (int step = 0; step < 10; step++) reader.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double endTarget = Math.Min(11 * pageHeight, scroll.ScrollableHeight);
            bool end = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && Math.Abs(scroll.VerticalOffset - endTarget) <= 1
                && reader.WebtoonRenderedLoadedCount > 0 && reader.WebtoonBlankStripCount == 0);
            int actualPage = reader.WebtoonCurrentPage;
            reader.TestFlushSave();
            saved = App.Library.GetProgress(book.Id);
            checks.Add(("末页11按真实底部clamp完成且DB跟实际视口", end && endTarget < 11 * pageHeight - 1 && saved is { Mode: "webtoon" }
                && saved.PageIndex == actualPage && reader.PageInfoText.Text == $"{actualPage + 1} / 12"
                && Math.Abs(saved.ScrollOffset - reader.WebtoonView.ScrollFraction) < 0.002,
                $"actualPage={actualPage};offset={scroll.VerticalOffset:F2};target={endTarget:F2};max={scroll.ScrollableHeight:F2};" + OpcDb(book)));
            Capture(reader, Path.Combine(outDir, "opc-webtoon-last-clamp.png"));

            bool firstHome = RaiseLogicAuditKey(reader, Key.Home);
            bool atHome = await WaitLogicAuditAsync(() => reader.IsWebtoonReady && reader.WebtoonCurrentPage == 0 && scroll.VerticalOffset < 1);
            bool sameHome = RaiseLogicAuditKey(reader, Key.Home);
            reader.TestWebtoonScrollBy(pageHeight * 2.25);
            bool manuallyMoved = await WaitLogicAuditAsync(() => reader.WebtoonCurrentPage == 2 && scroll.VerticalOffset > 2 * pageHeight);
            reader.TestSwitchToPaged();
            bool actualPaged = await WaitLogicAuditAsync(() => reader.LoadingPanel.Visibility == Visibility.Collapsed && OpcPixels(reader.PageImage, 2));
            reader.TestFlushSave();
            checks.Add(("同位置真实Home后手动滚动再切mode不回旧0", firstHome && atHome && sameHome && manuallyMoved
                && actualPaged && reader.PageInfoText.Text == "3 / 12" && OpcSaved(book, 2, "paged", 3.0 / 12),
                $"home={firstHome}/{atHome}/{sameHome};moved={manuallyMoved};pixels2={actualPaged};" + OpcDb(book)));
            Capture(reader, Path.Combine(outDir, "opc-same-home-manual-scroll-mode.png"));
        }
        catch (Exception ex) { checks.Add(("条漫真实offset完成边界组异常", false, ex.ToString())); }
        finally { reader?.Close(); }
    }
}
