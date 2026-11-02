using System.Windows;
using ComicVerse.Core;

namespace ComicVerse.App.Windows;

public partial class ReaderWindow
{
    private readonly Dictionary<int, bool> _comicWidePages = new();
    private readonly Dictionary<int, int> _comicPortraitRunStarts = new();
    private readonly Queue<int> _doubleNavigationSteps = new();
    private bool _doubleNavigationRunning;
    private long _doubleNavigationEpoch;
    private CancellationTokenSource? _doubleNavigationCts;

    private async Task<bool> IsComicPageWideAsync(int index, CancellationToken ct)
    {
        if (_comicWidePages.TryGetValue(index, out bool wide)) return wide;
        var loader = _loader;
        if (loader is null || index < 0 || index >= loader.PageCount) return false;
        // A source may be busy decompressing another page. Never wait for its
        // read lock on the UI thread just to decide the neighbouring spread.
        var size = await Task.Run(() => loader.GetPageSize(index), ct);
        ct.ThrowIfCancellationRequested();
        wide = size is { Width: var width, Height: var height } && width > height;
        if (size is not null) _comicWidePages[index] = wide;
        return wide;
    }

    private async Task<int> GetDoubleSpreadLengthAsync(int start, CancellationToken ct)
    {
        if (_loader is null || start >= _loader.PageCount - 1 || await IsComicPageWideAsync(start, ct)) return 1;
        // A landscape page stays alone even when preceded by a portrait page.
        return await IsComicPageWideAsync(start + 1, ct) ? 1 : 2;
    }

    private async Task<int> GetPreviousDoubleSpreadAsync(int start, CancellationToken ct)
        => await GetCanonicalDoubleSpreadAsync(Math.Max(0, start - 1), ct);

    private async Task<int> GetCanonicalDoubleSpreadAsync(int target, CancellationToken ct)
    {
        if (await IsComicPageWideAsync(target, ct)) return target;
        int runStart = target;
        bool allKnown = _comicWidePages.ContainsKey(target);
        while (runStart > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_comicPortraitRunStarts.TryGetValue(runStart, out int knownStart)) { runStart = knownStart; break; }
            bool precedingWide = await IsComicPageWideAsync(runStart - 1, ct);
            allKnown &= _comicWidePages.ContainsKey(runStart - 1);
            if (precedingWide) break;
            runStart--;
        }
        if (allKnown)
            for (int index = runStart; index <= target; index++) _comicPortraitRunStarts[index] = runStart;
        return runStart + (target - runStart) / 2 * 2;
    }

    private void CancelDoubleNavigation()
    {
        _doubleNavigationEpoch++;
        _doubleNavigationSteps.Clear();
        _doubleNavigationRunning = false;
        _doubleNavigationCts?.Cancel();
        _doubleNavigationCts = null;
    }

    private async void MoveDoubleSpread(int direction)
    {
        if (_closed || _mode != "double" || _loader is null) return;
        _doubleNavigationSteps.Enqueue(direction);
        if (_doubleNavigationRunning) return;
        _doubleNavigationRunning = true;
        long epoch = _doubleNavigationEpoch;
        using var cts = new CancellationTokenSource();
        _doubleNavigationCts = cts;
        bool IsCurrent() => !_closed && _mode == "double" && epoch == _doubleNavigationEpoch && !cts.IsCancellationRequested;
        try
        {
            while (_doubleNavigationSteps.Count > 0 && IsCurrent())
            {
                int step = _doubleNavigationSteps.Dequeue();
                int start = await GetCanonicalDoubleSpreadAsync(_spreadStart, cts.Token);
                int length = await GetDoubleSpreadLengthAsync(start, cts.Token);
                int target = step > 0
                    ? (start + length >= _loader.PageCount ? start : start + length)
                    : await GetPreviousDoubleSpreadAsync(start, cts.Token);
                if (!IsCurrent()) return;
                if (target == _spreadStart) continue;
                _spreadStart = target;
                RenderDoubleSpread();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                Log.Error("双页导航失败", ex);
                DoubleLoading.Visibility = Visibility.Visible;
                DoubleLoadingText.Text = "加载失败，请重试";
            }
        }
        finally
        {
            if (ReferenceEquals(_doubleNavigationCts, cts)) _doubleNavigationCts = null;
            if (epoch == _doubleNavigationEpoch) _doubleNavigationRunning = false;
        }
    }
}
