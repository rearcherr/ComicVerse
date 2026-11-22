using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ComicVerse.App.Updates;
using ComicVerse.App.Windows;
using ComicVerse.Core.Updates;

namespace ComicVerse.App;

public static partial class SmokeTest
{
    // Independent QA: HTTP replies are synthetic, and ordinary smoke never runs
    // a downloaded binary or exits for installation. The install protocol below
    // accepts only an explicitly scoped, locally frozen executable fixture.
    private static async Task<(bool Ok, string Detail)> CheckOnlineUpdateAsync(string outDir, string[] args)
    {
        if (args.Contains("--online-update-install-fixture"))
            return await OnlineInstallFixtureAsync(outDir, args);
        var checks = new List<(string Name, bool Ok, string Detail)>();
        var skipped = new List<string>();
        string originalTheme = ThemeService.Current;
        string databaseBefore = OnlineDatabaseFacts();
        try
        {
            await OnlineServiceChecksAsync(outDir, checks);
            await OnlineWindowChecksAsync(outDir, checks, skipped);
        }
        catch (Exception ex)
        {
            checks.Add(("unexpected fixture exception", false, ex.ToString()));
        }
        finally
        {
            ThemeService.Apply(originalTheme);
        }
        checks.Add(("checks/cancel/failure preserve library facts", databaseBefore == OnlineDatabaseFacts(), "books, progress, bookmarks and settings snapshot"));
        await File.WriteAllTextAsync(Path.Combine(outDir, "online-update-details.json"), JsonSerializer.Serialize(new
        {
            Checks = checks.Select(c => new { c.Name, c.Ok, c.Detail }),
            Skipped = skipped,
            InputKind = "Synthetic HTTP only; no remote executable download, installation or application exit"
        }, new JsonSerializerOptions { WriteIndented = true }));
        string detail = $"在线更新 {checks.Count(c => c.Ok)}/{checks.Count}; skipped={skipped.Count} — "
            + string.Join(" | ", checks.Select(c => $"{c.Name}={c.Ok}({c.Detail})"))
            + string.Join("", skipped.Select(s => " | SKIPPED: " + s));
        return (checks.Count > 0 && checks.All(c => c.Ok), detail);
    }

    private static string OnlineDatabaseFacts() => JsonSerializer.Serialize(new
    {
        Books = App.Library.GetBooks().OrderBy(b => b.Id).Select(b => new
        {
            b.Id, b.FilePath, Progress = App.Library.GetProgress(b.Id),
            Bookmarks = App.Library.GetBookmarks(b.Id)
        }),
        Settings = App.Library.GetSettings().OrderBy(p => p.Key)
    });

    private static string OnlineHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string OnlineFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static JsonObject OnlineMetadata(string tag, byte[] payload)
    {
        return new JsonObject
        {
            ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false,
            ["html_url"] = $"https://github.com/rearcherr/ComicVerse/releases/tag/{tag}",
            ["assets"] = new JsonArray(new JsonObject
            {
                ["name"] = "ComicVerse.exe", ["state"] = "uploaded", ["size"] = payload.Length,
                ["digest"] = "sha256:" + OnlineHash(payload).ToLowerInvariant(),
                ["browser_download_url"] = $"https://github.com/rearcherr/ComicVerse/releases/download/{tag}/ComicVerse.exe"
            })
        };
    }

    private static HttpResponseMessage OnlineJson(HttpRequestMessage request, JsonNode data)
        => OnlineResponse(request, Encoding.UTF8.GetBytes(data.ToJsonString()));

    private static HttpResponseMessage OnlineResponse(HttpRequestMessage request, byte[] bytes, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        if (declaredLength.HasValue) response.Content.Headers.ContentLength = declaredLength;
        return response;
    }

    private static GitHubUpdateService OnlineService(HttpClient client, int timeoutMs = 5000)
        => new(client, new UpdateServiceOptions
        {
            CheckTimeout = TimeSpan.FromMilliseconds(timeoutMs), DownloadTimeout = TimeSpan.FromMilliseconds(timeoutMs)
        });

    private static async Task<bool> OnlineThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(8)); return false; }
        catch (T) { return true; }
    }

    private static async Task OnlineCaseAsync(List<(string Name, bool Ok, string Detail)> checks, string name,
        Func<Task<(bool Ok, string Detail)>> operation)
    {
        try
        {
            var result = await operation();
            checks.Add((name, result.Ok, result.Detail));
        }
        catch (Exception ex) { checks.Add((name, false, ex.ToString())); }
    }

    private static async Task OnlineServiceChecksAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks)
    {
        byte[] payload = Enumerable.Range(0, 131072).Select(i => (byte)(i * 17 + 3)).ToArray();
        string directory = Path.Combine(outDir, "online-update-network-fixtures");
        Directory.CreateDirectory(directory);
        foreach (var item in new[]
        {
            (Tag:"v1.4.0", Current:new Version(1,4,0), Expected:false),
            (Tag:"v1.4.0", Current:new Version(1,3,2), Expected:true),
            (Tag:"v1.4.0", Current:new Version(1,5,0), Expected:false),
            (Tag:"v1.10.0", Current:new Version(1,9,0), Expected:true),
            (Tag:"v1.9.0", Current:new Version(1,10,0), Expected:false)
        })
        {
            await OnlineCaseAsync(checks, $"version {item.Current} -> {item.Tag}", async () =>
            {
                using var handler = new OnlineHandler((request, _) => Task.FromResult(OnlineJson(request, OnlineMetadata(item.Tag, payload))));
                using var client = new HttpClient(handler);
                using var service = OnlineService(client);
                var result = await service.CheckAsync(item.Current);
                return (result.IsUpdateAvailable == item.Expected && handler.Requests.SequenceEqual(new[] { GitHubUpdateService.LatestReleaseUri.AbsoluteUri }),
                    $"available={result.IsUpdateAvailable}; requests={handler.Requests.Count}; no download");
            });
        }

        var invalid = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("draft", n => n["draft"] = true),
            ("prerelease", n => n["prerelease"] = true),
            ("unsupported tag", n => n["tag_name"] = "v1.5.0-rc1"),
            ("wrong asset name", n => n["assets"]![0]!["name"] = "ComicVerse-arm64.exe"),
            ("asset not uploaded", n => n["assets"]![0]!["state"] = "new"),
            ("duplicate EXE", n => n["assets"]!.AsArray().Add(n["assets"]![0]!.DeepClone())),
            ("zero size", n => n["assets"]![0]!["size"] = 0),
            ("asset over resource cap", n => n["assets"]![0]!["size"] = 256L * 1024 * 1024 + 1),
            ("missing digest", n => n["assets"]![0]!.AsObject().Remove("digest")),
            ("malformed digest", n => n["assets"]![0]!["digest"] = "sha256:1234"),
            ("wrong repository URL", n => n["assets"]![0]!["browser_download_url"] = "https://github.com/elsewhere/ComicVerse/releases/download/v99.0.0/ComicVerse.exe"),
            ("insecure URL", n => n["assets"]![0]!["browser_download_url"] = "http://github.com/rearcherr/ComicVerse/releases/download/v99.0.0/ComicVerse.exe"),
            ("mismatched release page", n => n["html_url"] = "https://github.com/rearcherr/ComicVerse/releases/tag/v98.0.0")
        };
        foreach (var item in invalid)
        {
            await OnlineCaseAsync(checks, "reject metadata: " + item.Name, async () =>
            {
                var metadata = OnlineMetadata("v99.0.0", payload);
                item.Mutate(metadata);
                using var handler = new OnlineHandler((request, _) => Task.FromResult(OnlineJson(request, metadata)));
                using var client = new HttpClient(handler);
                using var service = OnlineService(client);
                bool rejected = await OnlineThrowsAsync<InvalidDataException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); });
                return (rejected && handler.Requests.Count == 1, "invalid stable asset rejected without download");
            });
        }

        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            await OnlineCaseAsync(checks, "HTTP " + (int)code, async () =>
            {
                using var handler = new OnlineHandler((request, _) => Task.FromResult(new HttpResponseMessage(code) { RequestMessage = request }));
                using var client = new HttpClient(handler);
                using var service = OnlineService(client);
                return (await OnlineThrowsAsync<HttpRequestException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); }), "HTTP failure is not an available update");
            });
        await OnlineCaseAsync(checks, "malformed JSON", async () =>
        {
            using var handler = new OnlineHandler((request, _) => Task.FromResult(OnlineResponse(request, Encoding.UTF8.GetBytes("{broken"))));
            using var client = new HttpClient(handler);
            using var service = OnlineService(client);
            return (await OnlineThrowsAsync<JsonException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); }), "parse failure retained");
        });
        await OnlineCaseAsync(checks, "metadata allocation bounded", async () =>
        {
            using var handler = new OnlineHandler((request, _) => Task.FromResult(OnlineResponse(request, [1], 2 * 1024 * 1024 + 1)));
            using var client = new HttpClient(handler);
            using var service = OnlineService(client);
            return (await OnlineThrowsAsync<InvalidDataException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); }), "oversized declaration rejected before buffering");
        });
        await OnlineCaseAsync(checks, "metadata actual stream bounded", async () =>
        {
            using var handler = new OnlineHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StreamContent(new OnlineBodyStream(new byte[2 * 1024 * 1024 + 1]))
            }));
            using var client = new HttpClient(handler);
            using var service = OnlineService(client);
            return (await OnlineThrowsAsync<InvalidDataException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); }), "unknown content length cannot bypass metadata cap");
        });
        await OnlineCaseAsync(checks, "connection failure", async () =>
        {
            using var handler = new OnlineHandler((_, _) => throw new HttpRequestException("QA synthetic offline connection"));
            using var client = new HttpClient(handler);
            using var service = OnlineService(client);
            return (await OnlineThrowsAsync<HttpRequestException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); }), "offline error retained without available release");
        });
        foreach (bool cancelled in new[] { false, true })
            await OnlineCaseAsync(checks, cancelled ? "caller cancels check" : "bounded check timeout", async () =>
            {
                using var handler = new OnlineHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); });
                using var client = new HttpClient(handler);
                using var service = OnlineService(client, cancelled ? 3000 : 180);
                using var cts = new CancellationTokenSource();
                if (cancelled) cts.CancelAfter(80);
                bool ok = cancelled
                    ? await OnlineThrowsAsync<OperationCanceledException>(async () => { await service.CheckAsync(new Version(1, 5, 0), cts.Token); })
                    : await OnlineThrowsAsync<TimeoutException>(async () => { await service.CheckAsync(new Version(1, 5, 0)); });
                return (ok && handler.ActiveCalls == 0, "caller cancellation distinguished from deadline; request settled");
            });

        foreach (string scenario in new[] { "complete", "wrong hash", "truncated", "overlong", "wrong content length", "stream failure", "preexisting stage", "cancel", "timeout", "untrusted redirect", "trusted redirect" })
            await OnlineCaseAsync(checks, "download " + scenario, async () =>
            {
                string path = Path.Combine(directory, scenario.Replace(' ', '-') + ".bin");
                if (File.Exists(path)) throw new InvalidOperationException("Fixture destination already exists.");
                byte[] sent = scenario == "truncated" ? payload[..^1] : scenario == "overlong" ? payload.Concat(new byte[] { 4 }).ToArray() : payload;
                var samples = new List<UpdateDownloadProgress>();
                var gate = scenario is "cancel" or "timeout" ? new OnlineReadGate() : null;
                var body = new OnlineBodyStream(sent, gate, scenario == "stream failure");
                int requests = 0;
                using var handler = new OnlineHandler((request, _) =>
                {
                    requests++;
                    if (scenario.EndsWith("redirect", StringComparison.Ordinal) && requests == 1)
                    {
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                        redirect.Headers.Location = new Uri(scenario == "trusted redirect"
                            ? "https://release-assets.githubusercontent.com/github-production-release-asset-qa/test.exe"
                            : "https://example.invalid/ComicVerse.exe");
                        return Task.FromResult(redirect);
                    }
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(body) };
                    if (scenario == "wrong content length") response.Content.Headers.ContentLength = payload.Length - 1;
                    return Task.FromResult(response);
                });
                using var client = new HttpClient(handler);
                using var service = OnlineService(client, scenario == "timeout" ? 180 : 5000);
                var release = new UpdateRelease(new Version(99, 0, 0), "v99.0.0",
                    new Uri("https://github.com/rearcherr/ComicVerse/releases/tag/v99.0.0"),
                    new Uri("https://github.com/rearcherr/ComicVerse/releases/download/v99.0.0/ComicVerse.exe"),
                    payload.Length, scenario == "wrong hash" ? new string('0', 64) : OnlineHash(payload));
                if (scenario == "preexisting stage") await File.WriteAllTextAsync(path, "preexisting QA sentinel");
                using var cts = new CancellationTokenSource();
                var operation = service.DownloadAsync(release, path, new OnlineProgress(samples), cts.Token);
                try
                {
                    if (scenario == "cancel")
                    {
                        await gate!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        cts.Cancel();
                    }
                    if (scenario is "complete" or "trusted redirect")
                    {
                        var result = await operation;
                        bool progress = samples.Count >= 2 && samples[0].BytesReceived == 0
                            && samples[^1].BytesReceived == payload.Length && samples[^1].Fraction == 1
                            && samples.Zip(samples.Skip(1)).All(p => p.First.BytesReceived <= p.Second.BytesReceived);
                        return (result.Size == payload.Length && result.Sha256 == OnlineHash(payload)
                            && File.ReadAllBytes(path).SequenceEqual(payload) && progress
                            && requests == (scenario == "trusted redirect" ? 2 : 1), $"bytes={result.Size}; progressEvents={samples.Count}; requests={requests}; synthetic non-PE bytes never executed");
                    }
                    bool failed = scenario switch
                    {
                        "preexisting stage" => await OnlineThrowsAsync<IOException>(async () => { await operation; }),
                        "stream failure" => await OnlineThrowsAsync<IOException>(async () => { await operation; }),
                        "cancel" => await OnlineThrowsAsync<OperationCanceledException>(async () => { await operation; }),
                        "timeout" => await OnlineThrowsAsync<TimeoutException>(async () => { await operation; }),
                        _ => await OnlineThrowsAsync<InvalidDataException>(async () => { await operation; })
                    };
                    bool clean = scenario == "preexisting stage" ? File.ReadAllText(path) == "preexisting QA sentinel" : !File.Exists(path);
                    return (failed && clean && (scenario != "untrusted redirect" || requests == 1), $"rejected={failed}; own partial removed/preexisting retained={clean}; requests={requests}");
                }
                finally { gate?.Release.TrySetResult(true); }
            });
    }

    private static T OnlineControl<T>(UpdateWindow window, string name) where T : FrameworkElement
        => window.FindName(name) as T ?? throw new InvalidOperationException("Missing update UI control: " + name);
    private static string OnlineStatus(UpdateWindow window) => OnlineControl<TextBlock>(window, "StatusText").Text;
    private static void OnlineClick(UpdateWindow window, string name)
    {
        var button = OnlineControl<Button>(window, name);
        if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Unavailable update action: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    private static UpdateWindow OnlineWindow(GitHubUpdateService service)
        => new(new AppUpdateCoordinator(service)) { Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() };

    private static async Task OnlineWindowChecksAsync(string outDir, List<(string Name, bool Ok, string Detail)> checks, List<string> skipped)
    {
        byte[] payload = Enumerable.Repeat((byte)0x5a, 131072).ToArray();
        foreach (bool newer in new[] { false, true })
            await OnlineCaseAsync(checks, newer ? "UI discovers without auto download" : "UI current version", async () =>
            {
                string tag = newer ? "v99.0.0" : "v" + App.AppVersion;
                using var handler = new OnlineHandler((request, _) => Task.FromResult(OnlineJson(request, OnlineMetadata(tag, payload))));
                using var client = new HttpClient(handler);
                var window = OnlineWindow(OnlineService(client));
                try
                {
                    window.Show();
                    bool ready = await WaitLogicAuditAsync(() => OnlineControl<Button>(window, "ActionButton").IsEnabled, 5);
                    bool correct = ready && OnlineStatus(window) == (newer ? "发现新版本" : "当前已是最新版本")
                        && OnlineControl<TextBlock>(window, "LatestVersionText").Text == tag
                        && handler.Requests.Count == 1 && window.IsVisible;
                    if (newer)
                    {
                        ThemeService.Apply("light"); window.Width = 500; window.Height = 430;
                        await Task.Delay(100);
                        Capture(window, Path.Combine(outDir, "online-update-new-minimum-light.png"));
                        correct &= OnlineControl<Button>(window, "ActionButton").ActualWidth > 0
                            && OnlineControl<TextBlock>(window, "PackageSizeText").IsVisible;
                    }
                    return (correct, $"status={OnlineStatus(window)}; requests={handler.Requests.Count}; visible={window.IsVisible}");
                }
                finally { window.Close(); }
            });

        await OnlineCaseAsync(checks, "UI cancelled late check cannot advertise update", async () =>
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new OnlineHandler(async (request, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(3)); // deliberately late reply, not a production delay
                return OnlineJson(request, OnlineMetadata("v99.0.0", payload));
            });
            using var client = new HttpClient(handler);
            var window = OnlineWindow(OnlineService(client));
            try
            {
                window.Show();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                bool busy = !OnlineControl<Button>(window, "ActionButton").IsEnabled;
                OnlineClick(window, "CancelButton");
                release.TrySetResult(true);
                bool done = await WaitLogicAuditAsync(() => OnlineControl<Button>(window, "ActionButton").IsEnabled, 4);
                return (busy && done && OnlineStatus(window) == "已取消检查" && window.IsVisible && handler.ActiveCalls == 0,
                    $"status={OnlineStatus(window)}; inFlight={handler.ActiveCalls}; visible={window.IsVisible}");
            }
            finally { release.TrySetResult(true); window.Close(); }
        });

        await OnlineCaseAsync(checks, "UI network failure retries", async () =>
        {
            int calls = 0;
            using var handler = new OnlineHandler((request, _) => Task.FromResult(++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request }
                : OnlineJson(request, OnlineMetadata("v" + App.AppVersion, payload))));
            using var client = new HttpClient(handler);
            var window = OnlineWindow(OnlineService(client));
            try
            {
                window.Show();
                bool failed = await WaitLogicAuditAsync(() => OnlineStatus(window) == "暂时无法检查更新", 4);
                OnlineClick(window, "ActionButton");
                bool retried = await WaitLogicAuditAsync(() => OnlineStatus(window) == "当前已是最新版本", 4);
                return (failed && retried && window.IsVisible && calls == 2, $"failed={failed}; retried={retried}; calls={calls}");
            }
            finally { window.Close(); }
        });

        await OnlineCaseAsync(checks, "closing UI cancels late check and keeps main window", async () =>
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new OnlineHandler(async (request, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return OnlineJson(request, OnlineMetadata("v99.0.0", payload));
            });
            using var client = new HttpClient(handler);
            var window = OnlineWindow(OnlineService(client));
            try
            {
                window.Show();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                window.Close();
                release.TrySetResult(true);
                bool settled = await WaitLogicAuditAsync(() => handler.ActiveCalls == 0, 3);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                bool main = Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible);
                return (settled && !window.IsVisible && main && OnlineControl<TextBlock>(window, "LatestVersionText").Text == "—",
                    $"settled={settled}; dialogVisible={window.IsVisible}; mainVisible={main}; late result not published");
            }
            finally { release.TrySetResult(true); if (window.IsVisible) window.Close(); }
        });

        if (File.Exists(Path.ChangeExtension(Environment.ProcessPath!, ".runtimeconfig.json")))
        {
            skipped.Add("framework-dependent host: UI download/cancel/invalid-PE requires final self-contained candidate; service and check UI executed");
            return;
        }
        string executable = Environment.ProcessPath!;
        string exeBefore = OnlineFileHash(executable);
        foreach (string scenario in new[] { "cancel", "bad hash", "invalid PE" })
            await OnlineCaseAsync(checks, "UI download " + scenario + " keeps application", async () =>
            {
                var gate = scenario == "cancel" ? new OnlineReadGate() : null;
                using var handler = new OnlineHandler((request, _) =>
                {
                    if (request.RequestUri == GitHubUpdateService.LatestReleaseUri)
                    {
                        var metadata = OnlineMetadata("v99.0.0", payload);
                        if (scenario == "bad hash") metadata["assets"]![0]!["digest"] = "sha256:" + new string('0', 64);
                        return Task.FromResult(OnlineJson(request, metadata));
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request, Content = new StreamContent(new OnlineBodyStream(payload, gate))
                    });
                });
                using var client = new HttpClient(handler);
                var window = OnlineWindow(OnlineService(client));
                try
                {
                    window.Show();
                    if (!await WaitLogicAuditAsync(() => OnlineStatus(window) == "发现新版本", 4))
                        return (false, "new release was not displayed");
                    OnlineClick(window, "ActionButton");
                    bool progress = true;
                    if (gate is not null)
                    {
                        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        progress = await WaitLogicAuditAsync(() => OnlineControl<ProgressBar>(window, "DownloadProgressBar").Value > 0, 2);
                        OnlineClick(window, "CancelButton");
                    }
                    bool settled = await WaitLogicAuditAsync(() => OnlineStatus(window) == (gate is null ? "更新下载失败" : "已取消下载"), 6);
                    Capture(window, Path.Combine(outDir, "online-update-" + scenario.Replace(' ', '-') + ".png"));
                    bool unchanged = OnlineFileHash(executable) == exeBefore;
                    return (settled && progress && unchanged && window.IsVisible && OnlineControl<Button>(window, "ActionButton").IsEnabled,
                        $"status={OnlineStatus(window)}; progressSeen={progress}; exeUnchanged={unchanged}; visible={window.IsVisible}");
                }
                finally { gate?.Release.TrySetResult(true); window.Close(); }
            });
    }

    private sealed class OnlineHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal readonly List<string> Requests = [];
        private int _active;
        internal int ActiveCalls => Volatile.Read(ref _active);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            Interlocked.Increment(ref _active);
            try { return await reply(request, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class OnlineProgress(List<UpdateDownloadProgress> values) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => values.Add(value);
    }
    private sealed class OnlineReadGate
    {
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class OnlineBodyStream(byte[] bytes, OnlineReadGate? gate = null, bool fail = false) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position > 0 && gate is not null)
            {
                gate.Entered.TrySetResult(true);
                await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_position > 0 && fail) throw new IOException("QA injected download stream failure");
            int count = Math.Min(Math.Min(buffer.Length, 8192), bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("QA download expects asynchronous reads");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(bool Ok, string Detail)> OnlineInstallFixtureAsync(string outDir, string[] args)
    {
        string ReadArgument(string prefix) => args.SingleOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            ?? throw new InvalidDataException("Missing scoped install-fixture argument: " + prefix);
        string scope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadArgument("--update-fixture-root=")));
        string payload = Path.GetFullPath(ReadArgument("--update-fixture-exe="));
        string expectedHash = ReadArgument("--update-fixture-sha=").ToUpperInvariant();
        string target = Path.GetFullPath(Environment.ProcessPath!);
        bool Inside(string path) => Path.GetFullPath(path).StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!Environment.GetCommandLineArgs().Contains("--smoke") || scope.Length <= Path.GetPathRoot(scope)!.Length
            || !Inside(target) || !Inside(payload) || !Inside(outDir) || !Inside(App.AppDataDir)
            || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)
            || OnlineFileHash(payload) != expectedHash)
            throw new InvalidDataException("Install fixture must use only its isolated root, data, output and approved local payload.");
        string targetVersionText = FileVersionInfo.GetVersionInfo(payload).ProductVersion ?? "";
        if (!Version.TryParse(targetVersionText, out var targetVersion) || targetVersion <= Version.Parse(App.AppVersion))
            throw new InvalidDataException("Install fixture must carry a genuinely newer PE product version.");
        long length = new FileInfo(payload).Length;
        string tag = "v" + targetVersion.ToString(3);
        var metadata = OnlineMetadata(tag, []);
        metadata["assets"]![0]!["size"] = length;
        metadata["assets"]![0]!["digest"] = "sha256:" + expectedHash.ToLowerInvariant();
        using var handler = new OnlineHandler((request, _) => Task.FromResult(request.RequestUri == GitHubUpdateService.LatestReleaseUri
            ? OnlineJson(request, metadata)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StreamContent(File.OpenRead(payload))
            }));
        using var client = new HttpClient(handler);
        var book = await OpcImportAsync(OpcBrokenComic(outDir, "升级交接进度", 6));
        App.Library.SaveProgress(book.Id, 1.0 / 6, 0, 0, 0, "paged", 1);
        var reader = new ReaderWindow(book) { Width = 980, Height = 720, ShowInTaskbar = false,
            Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() };
        reader.Show();
        if (!await WaitLogicAuditAsync(() => reader.IsComicPageLoaded, 5)) return (false, "fixture reader failed to load");
        reader.TestFlushSave();
        reader.TestJumpToPage(3);
        if (!await WaitLogicAuditAsync(() => reader.IsComicPageLoaded && OpcPixels(reader.PageImage, 3), 5))
            return (false, "fixture current successful page did not render");
        var window = OnlineWindow(OnlineService(client, 30000));
        window.Show();
        if (!await WaitLogicAuditAsync(() => OnlineStatus(window) == "发现新版本", 5)) return (false, "fixture release not offered");
        await File.WriteAllTextAsync(Path.Combine(outDir, "online-update-install-prepared.json"), JsonSerializer.Serialize(new
        {
            Environment = App.GetSmokeProcessEnvironment(), Executable = target, OldHash = OnlineFileHash(target), OldVersion = App.AppVersion,
            Fixture = payload, NewHash = expectedHash, NewVersion = targetVersion.ToString(3), DataDir = App.AppDataDir,
            BookId = book.Id, BookPath = book.FilePath, Page = 3, Mode = "paged", ParentPid = Environment.ProcessId,
            ExpectedPage = 3, ExpectedMode = "paged", BeforeProgress = App.Library.GetProgress(book.Id),
            ExpectedLibraryBookCount = App.Library.GetBooks().Count, ActualFooter = reader.PageInfoText.Text, Scope = scope
        }, new JsonSerializerOptions { WriteIndented = true }));
        OnlineClick(window, "ActionButton");
        bool error = await WaitLogicAuditAsync(() => OnlineStatus(window).Contains("失败", StringComparison.Ordinal)
            || OnlineStatus(window).Contains("无法", StringComparison.Ordinal), 45);
        // Successful handoff terminates this actual App before this continuation.
        if (window.IsVisible) window.Close();
        if (reader.IsVisible) reader.Close();
        return (false, error ? "install fixture UI reported: " + OnlineStatus(window) : "old App did not exit within bounded handoff deadline");
    }
}
