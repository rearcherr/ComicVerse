using System.IO;
using System.Windows;
using ComicVerse.App.Windows;
using ComicVerse.Core.Updates;

namespace ComicVerse.App.Updates;

internal sealed class AppUpdateCoordinator : IDisposable
{
    private readonly GitHubUpdateService _service;

    internal AppUpdateCoordinator() : this(new GitHubUpdateService()) { }

    internal AppUpdateCoordinator(GitHubUpdateService service) => _service = service;

    internal Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _service.CheckAsync(Version.Parse(App.AppVersion), ct);

    internal async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        string target = AppUpdateInstaller.CurrentExecutable();
        string stage = AppUpdateInstaller.CreateStage(target);
        try
        {
            var update = await _service.DownloadAsync(release, Path.Combine(stage, AppUpdateInstaller.CandidateName), progress, ct)
                .ConfigureAwait(false);
            await Task.Run(() => AppUpdateInstaller.Prepare(update, target), ct).ConfigureAwait(false);
            return update;
        }
        catch
        {
            AppUpdateInstaller.CleanStage(stage);
            throw;
        }
    }

    internal async Task RestartToInstallAsync(DownloadedUpdate update)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (App.Importer.IsImporting) throw new InvalidOperationException("书籍正在导入，请等待导入完成后重试更新。");
        string target = AppUpdateInstaller.CurrentExecutable();
        AppUpdateInstaller.RequireNoOtherInstance(target);
        foreach (var reader in Application.Current.Windows.OfType<ReaderWindow>().ToArray())
            reader.SaveBeforeUpdate();

        var backup = Path.Combine(App.AppDataDir, $"library-before-update-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        await Task.Run(() => App.Library.Backup(backup));

        await AppUpdateInstaller.StartHelperAsync(update, target);
        ((App)Application.Current).ExitForUpdate();
    }

    internal void Discard(DownloadedUpdate update)
    {
        string? stage = Path.GetDirectoryName(update.FilePath);
        if (stage is not null && AppUpdateInstaller.IsStageFor(stage, AppUpdateInstaller.CurrentExecutable()))
            AppUpdateInstaller.CleanStage(stage);
    }

    public void Dispose() => _service.Dispose();
}
