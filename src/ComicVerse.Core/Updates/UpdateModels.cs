namespace ComicVerse.Core.Updates;

/// <summary>A stable release and the exact uploaded EXE whose GitHub SHA256 was validated.</summary>
public sealed record UpdateRelease(
    Version Version,
    string TagName,
    Uri ReleaseUri,
    Uri DownloadUri,
    long Size,
    string Sha256);

public sealed record UpdateCheckResult(bool IsUpdateAvailable, Version CurrentVersion, UpdateRelease Release);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}

public sealed record DownloadedUpdate(string FilePath, UpdateRelease Release, long Size, string Sha256);

public sealed record UpdateServiceOptions
{
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
