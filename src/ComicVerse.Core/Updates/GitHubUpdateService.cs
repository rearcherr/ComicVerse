using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ComicVerse.Core.Updates;

/// <summary>
/// Checks the fixed public repository and stages a verified EXE; never launches or replaces it.
/// Injected HttpClients must disable automatic redirects, as the default client does.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string RepositoryOwner = "rearcherr";
    public const string RepositoryName = "ComicVerse";
    public const string AssetName = "ComicVerse.exe";
    public const long MaxAssetBytes = 256L * 1024 * 1024;
    public const int MaxMetadataBytes = 2 * 1024 * 1024;
    public static Uri LatestReleaseUri { get; } = new("https://api.github.com/repos/rearcherr/ComicVerse/releases/latest");

    private static readonly Regex StableTag = new(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Sha256Digest = new(@"\Asha256:([0-9a-fA-F]{64})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Sha256Hex = new(@"\A[0-9a-fA-F]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;
    private bool _disposed;

    public GitHubUpdateService(HttpClient? httpClient = null, UpdateServiceOptions? options = null)
    {
        options ??= new UpdateServiceOptions();
        ValidateTimeout(options.CheckTimeout, nameof(options.CheckTimeout));
        ValidateTimeout(options.DownloadTimeout, nameof(options.DownloadTimeout));
        _checkTimeout = options.CheckTimeout;
        _downloadTimeout = options.DownloadTimeout;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (currentVersion.Build < 0 || currentVersion.Revision > 0)
            throw new ArgumentException("当前版本必须是三段稳定版本。", nameof(currentVersion));
        var current = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_checkTimeout);
        try
        {
            using var request = CreateRequest(LatestReleaseUri, metadata: true);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            VerifyEffectiveUri(response, LatestReleaseUri, allowCdn: false);
            RequireOk(response);
            if (response.Content.Headers.ContentLength is long length && length > MaxMetadataBytes)
                throw new InvalidDataException("更新信息过大，已停止检查。");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (metadata.Length + read > MaxMetadataBytes)
                    throw new InvalidDataException("更新信息过大，已停止检查。");
                metadata.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(metadata.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var release = ParseRelease(json.RootElement);
            deadline.Token.ThrowIfCancellationRequested();
            return new UpdateCheckResult(release.Version > current, current, release);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("检查更新超时，请稍后重试。", ex);
        }
    }

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, string stagingPath,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        string fullPath = Path.GetFullPath(stagingPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_downloadTimeout);
        bool created = false;
        try
        {
            using var response = await OpenDownloadAsync(release.DownloadUri, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("下载文件大小与发布信息不一致。");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] digest;
            long received = 0;
            // Never overwrite an existing file; only delete a file created by this invocation.
            await using (var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                progress?.Report(new UpdateDownloadProgress(0, release.Size));
                int read;
                while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + read);
                    if (received > release.Size || received > MaxAssetBytes)
                        throw new InvalidDataException("下载数据超过发布文件大小，已停止下载。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(new UpdateDownloadProgress(received, release.Size));
                }
                if (received != release.Size)
                    throw new InvalidDataException("下载未完成，文件大小与发布信息不一致。");
                digest = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("下载文件的 SHA256 校验失败，请重新检查更新。");
                await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return new DownloadedUpdate(fullPath, release, received, Convert.ToHexString(digest));
        }
        catch (Exception failure)
        {
            if (created)
            {
                try { File.Delete(fullPath); }
                catch (Exception cleanup)
                {
                    throw new IOException("更新下载失败，且无法清理暂存文件：" + fullPath,
                        new AggregateException(failure, cleanup));
                }
            }
            if (failure is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException("下载更新超时，请稍后重试。", failure);
            throw;
        }
    }

    public static bool TryParseStableTag(string? tag, out Version? version)
    {
        version = null;
        if (tag is null || tag.Length > 64) return false;
        var match = StableTag.Match(tag);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;
        version = new Version(major, minor, patch);
        return true;
    }

    private static UpdateRelease ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("更新信息不是公开的稳定版本。");
        string tag = GetString(root, "tag_name");
        if (!TryParseStableTag(tag, out var version))
            throw new InvalidDataException("发布版本号不是受支持的三段稳定版本。");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("发布信息缺少更新文件。");
        JsonElement? selected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || GetOptionalString(asset, "name") != AssetName) continue;
            if (selected is not null) throw new InvalidDataException("发布信息含有重复的更新文件。");
            selected = asset;
        }
        if (selected is not JsonElement exe || GetOptionalString(exe, "state") != "uploaded")
            throw new InvalidDataException("该版本没有上传完整的 ComicVerse.exe。");
        if (!exe.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number
            || !sizeValue.TryGetInt64(out long size))
            throw new InvalidDataException("更新文件大小无效。");
        var digest = Sha256Digest.Match(GetString(exe, "digest"));
        if (!digest.Success) throw new InvalidDataException("该版本缺少有效的 SHA256 校验信息。");
        if (!Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var releaseUri)
            || !Uri.TryCreate(GetString(exe, "browser_download_url"), UriKind.Absolute, out var downloadUri))
            throw new InvalidDataException("更新链接无效。");
        var release = new UpdateRelease(version!, tag, releaseUri, downloadUri, size, digest.Groups[1].Value.ToUpperInvariant());
        ValidateRelease(release);
        return release;
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri initial, CancellationToken ct)
    {
        Uri current = initial;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsTrustedDownloadHop(current, initial)) throw new InvalidDataException("更新下载重定向到了不可信的地址。");
            using var request = CreateRequest(current, metadata: false);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                VerifyEffectiveUri(response, current, allowCdn: true, initial: initial);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("更新下载重定向缺少地址。");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    continue;
                }
                RequireOk(response);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        throw new InvalidDataException("更新下载重定向次数过多。");
    }

    private static HttpRequestMessage CreateRequest(Uri uri, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ComicVerse-Updater/1.0");
        request.Headers.Accept.ParseAdd(metadata ? "application/vnd.github+json" : "application/octet-stream");
        if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        if (!TryParseStableTag(release.TagName, out var version) || !version!.Equals(release.Version)
            || release.Size <= 0 || release.Size > MaxAssetBytes || release.Sha256 is null || !Sha256Hex.IsMatch(release.Sha256))
            throw new InvalidDataException("更新版本、文件大小或 SHA256 校验信息无效。");
        var expectedRelease = new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/tag/{release.TagName}");
        var expectedDownload = new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/download/{release.TagName}/{AssetName}");
        if (!SameUri(release.ReleaseUri, expectedRelease) || !SameUri(release.DownloadUri, expectedDownload))
            throw new InvalidDataException("更新链接不属于指定仓库、版本和 ComicVerse.exe 文件。");
    }

    private static bool IsTrustedDownloadHop(Uri uri, Uri initial)
    {
        if (!IsHttps(uri)) return false;
        if (SameUri(uri, initial)) return true;
        return (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith("/github-production-release-asset", StringComparison.Ordinal);
    }

    private static void VerifyEffectiveUri(HttpResponseMessage response, Uri expected, bool allowCdn, Uri? initial = null)
    {
        // Native handlers populate RequestMessage; fake handlers need not. The production handler never auto-redirects.
        if (response.RequestMessage?.RequestUri is not Uri actual) return;
        if (SameUri(actual, expected)) return;
        if (allowCdn && initial is not null && IsTrustedDownloadHop(actual, initial)) return;
        throw new InvalidDataException("更新服务器返回了不可信的请求地址。");
    }

    private static bool SameUri(Uri? actual, Uri expected) => actual is not null && IsHttps(actual)
        && string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static void RequireOk(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("更新服务器没有返回完整文件或信息。");
    }

    private static string GetString(JsonElement element, string property)
        => GetOptionalString(element, property) ?? throw new InvalidDataException("更新信息缺少字段：" + property);

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, "更新操作必须设置有限的正超时。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
