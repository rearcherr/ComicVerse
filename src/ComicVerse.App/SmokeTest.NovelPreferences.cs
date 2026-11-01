using ComicVerse.App.Windows;
using ComicVerse.Core;
using ComicVerse.Core.Models;
using ComicVerse.Core.Services;

namespace ComicVerse.App;

public static partial class SmokeTest
{
    private static async Task<bool> WaitForNovelPreferencesLayoutAsync(ReaderWindow reader)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (reader.IsNovelReady && reader.IsNovelChapterLayoutStable) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static bool SameNovelFormatting(NovelViewSettings a, NovelViewSettings b) =>
        a.FontFamily == b.FontFamily && Math.Abs(a.FontSize - b.FontSize) < 0.01
        && Math.Abs(a.LineSpacing - b.LineSpacing) < 0.01
        && Math.Abs(a.ParagraphSpacing - b.ParagraphSpacing) < 0.01
        && Math.Abs(a.PageMargin - b.PageMargin) < 0.01
        && a.TextColor == b.TextColor && a.Background == b.Background;

    private static async Task<(bool Formatting, bool Wheel, string Detail)> CheckNovelPreferencesAsync(Book book)
    {
        ReaderWindow? reader = null;
        var globalBefore = App.Settings.NovelSettings;
        try
        {
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            if (!await WaitForNovelPreferencesLayoutAsync(reader)) return (false, false, "初始排版未就绪");
            reader.TestNovelGoToChapter(2);
            reader.NovelModeScroll.IsChecked = true;
            if (!await WaitForNovelPreferencesLayoutAsync(reader)) return (false, false, "滚动排版未就绪");

            double Offset() => reader.NovelActualScrollFraction * reader.NovelScrollableHeight;
            reader.TestNovelScrollViewportToFraction(0.25);
            await Task.Delay(200);
            double initial = Offset();
            bool handled = reader.TestNovelMouseWheel(-120);
            await Task.Delay(150);
            double fullStep = Offset() - initial;
            double beforeHalf = Offset();
            handled &= reader.TestNovelMouseWheel(-60);
            await Task.Delay(150);
            double halfStep = Offset() - beforeHalf;
            double beforeBurst = Offset();
            for (int i = 0; i < 4; i++) handled &= reader.TestNovelMouseWheel(-120);
            await Task.Delay(150);
            double burst = Offset() - beforeBurst;
            double beforeReverse = Offset();
            handled &= reader.TestNovelMouseWheel(120);
            await Task.Delay(150);
            double reverse = Offset() - beforeReverse;
            reader.TestNovelMouseWheel(-120); // 不等目标落地，立即进行一次外部定位。
            reader.TestNovelScrollViewportToFraction(0.1);
            await Task.Delay(150);
            double externalBase = Offset();
            reader.TestNovelMouseWheel(-120);
            await Task.Delay(150);
            bool externalReset = Math.Abs(Offset() - externalBase - 144) < 2;

            int chapter = reader.NovelChapterIndex;
            reader.TestNovelScrollViewportToFraction(0.99);
            await Task.Delay(150);
            reader.TestNovelMouseWheel(-120);
            await Task.Delay(150);
            bool stopsAtBottom = reader.NovelChapterIndex == chapter && reader.NovelActualScrollFraction > 0.999;
            reader.TestNovelMouseWheel(-120);
            await WaitForNovelPreferencesLayoutAsync(reader);
            bool nextChapter = reader.NovelChapterIndex == chapter + 1;
            reader.TestNovelScrollViewportToFraction(0.01);
            await Task.Delay(150);
            reader.TestNovelMouseWheel(120);
            await Task.Delay(150);
            bool stopsAtTop = reader.NovelChapterIndex == chapter + 1 && reader.NovelActualScrollFraction < 0.001;
            reader.TestNovelMouseWheel(120);
            await WaitForNovelPreferencesLayoutAsync(reader);
            bool previousChapter = reader.NovelChapterIndex == chapter && reader.NovelActualScrollFraction > 0.999;
            reader.NovelModePaged.IsChecked = true;
            await WaitForNovelPreferencesLayoutAsync(reader);
            int pagedChapter = reader.NovelChapterIndex, pagedPage = reader.NovelPage;
            bool pagedIgnored = !reader.TestNovelMouseWheel(-120);
            await Task.Delay(100);
            pagedIgnored &= reader.NovelChapterIndex == pagedChapter && reader.NovelPage == pagedPage;
            bool wheelOk = handled && Math.Abs(fullStep - 144) < 2 && Math.Abs(halfStep - 72) < 2
                && Math.Abs(burst - 576) < 2 && Math.Abs(reverse + 144) < 2
                && stopsAtBottom && nextChapter && stopsAtTop && previousChapter && pagedIgnored && externalReset;

            var expected = reader.NovelFormattingSnapshot;
            expected.FontFamily = reader.NovelFontChoices.FirstOrDefault(f => f == "Arial" && f != expected.FontFamily)
                ?? reader.NovelFontChoices.First(f => f != expected.FontFamily);
            expected.FontSize = 26;
            expected.LineSpacing = 1.9;
            expected.ParagraphSpacing = 14;
            expected.PageMargin = 96;
            expected.TextColor = "#F5E6C8";
            expected.Background = "#FBF4E8";
            reader.TestSetNovelFormatting(expected);
            await Task.Delay(650);
            await WaitForNovelPreferencesLayoutAsync(reader);
            reader.Close();
            reader = null;

            // 重建缓存服务，确保恢复来自数据库，而不是仍留在内存中的对象。
            App.Settings = new AppSettingsService(App.Library);
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool restored = await WaitForNovelPreferencesLayoutAsync(reader)
                && SameNovelFormatting(reader.NovelFormattingSnapshot, expected)
                && Math.Abs(reader.NovelDocumentMargin - 96) < 0.01
                && Math.Abs(reader.NovelDocumentFontSize - 26) < 0.01
                && reader.NovelDocumentFontFamily == expected.FontFamily;
            reader.TestSetNovelMargin(104);
            reader.Close(); // 不等 350ms 防抖，仍应保留最后一次改动。
            reader = null;
            App.Settings = new AppSettingsService(App.Library);
            expected.PageMargin = 104;
            reader = new ReaderWindow(book) { ShowInTaskbar = false };
            reader.Show();
            bool quickCloseRestored = await WaitForNovelPreferencesLayoutAsync(reader)
                && SameNovelFormatting(reader.NovelFormattingSnapshot, expected)
                && Math.Abs(reader.NovelDocumentMargin - 104) < 0.01;
            bool defaultsIntact = SameNovelFormatting(App.Settings.NovelSettings, globalBefore);
            string detail = $"每格 {fullStep:F0}px/半格 {halfStep:F0}px/四格 {burst:F0}px/反向 {reverse:F0}px; "
                + $"外部定位清理={externalReset},章尾={stopsAtBottom}/{nextChapter},章首={stopsAtTop}/{previousChapter},翻页忽略={pagedIgnored}; "
                + $"7字段恢复={restored},立即关闭恢复={quickCloseRestored},默认独立={defaultsIntact}";
            return (restored && quickCloseRestored && defaultsIntact, wheelOk, detail);
        }
        catch (Exception ex)
        {
            Log.Error("小说排版与滚轮回归失败", ex);
            return (false, false, ex.Message);
        }
        finally
        {
            reader?.Close();
        }
    }
}
