using System.Reflection;
using FontA.Core.Security;
using Velopack;
using Velopack.Sources;

namespace FontA.App;

public enum UpdateAvailability
{
    /// <summary>Updates can be checked for and installed.</summary>
    Ready,
    /// <summary>No update source is configured (no GitHub repository).</summary>
    NoSource,
    /// <summary>This copy wasn't installed by FontA's installer (for example a development build), so it can't update itself.</summary>
    NotInstalled
}

public enum UpdateStatus { UpToDate, Downloaded, Failed, Unavailable }

public sealed record UpdateResult(UpdateStatus Status, string Message, string? Version = null);

/// <summary>
/// Checks GitHub Releases for a newer FontA and downloads it with Velopack. A downloaded update is applied when FontA
/// exits (optionally restarting it), so nothing is ever replaced under a running app.
/// Only copies installed with FontA's Setup.exe can update; a build you run from Visual Studio or `dotnet run` cannot.
/// </summary>
public sealed class UpdateService
{
#if DEBUG
    /// <summary>
    /// Test hook: a folder or URL to use instead of GitHub. Compiled into debug builds only. In a release build an
    /// environment variable must never be able to redirect where updates come from.
    /// </summary>
    public const string SourceOverrideVariable = "FONTA_UPDATE_SOURCE";
#endif

    private readonly UpdateManager? _manager;
    private VelopackAsset? _downloaded;

    public UpdateService()
    {
        try
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } custom)
                _manager = new UpdateManager(custom);
            else
#endif
            if (RepoUrlPolicy.IsTrusted(RepoUrl))
                _manager = new UpdateManager(new GithubSource(RepoUrl!, accessToken: null, prerelease: false));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            _manager = null; // a malformed repository address: behave as if there were no source
        }
    }

    /// <summary>The GitHub repository this build checks, from the UpdateRepoUrl project property.</summary>
    public static string? RepoUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value is { Length: > 0 } v ? v : null;

    public UpdateAvailability Availability =>
        _manager == null ? UpdateAvailability.NoSource
        : !_manager.IsInstalled ? UpdateAvailability.NotInstalled
        : UpdateAvailability.Ready;

    public bool IsReady => Availability == UpdateAvailability.Ready;

    public string Describe() => Availability switch
    {
        UpdateAvailability.NoSource => "No update source is set. Builds made by the release workflow check their own GitHub repository.",
        UpdateAvailability.NotInstalled => "This copy of FontA wasn't installed with its installer, so it can't update itself. Install it from a GitHub release to get updates.",
        _ => "Checks GitHub for new versions and installs them when you quit FontA."
    };

    /// <summary>The installed version, or null for a copy that wasn't installed.</summary>
    public string? InstalledVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>A version that has been downloaded and will be installed when FontA exits.</summary>
    public string? PendingVersion => (_downloaded ?? _manager?.UpdatePendingRestart)?.Version?.ToString();

    public bool HasPending => PendingVersion != null;

    /// <summary>Looks for a newer release and downloads it. Never throws: problems come back as a message.</summary>
    public async Task<UpdateResult> CheckAndDownloadAsync(Action<int>? progress = null, CancellationToken ct = default)
    {
        if (!IsReady) return new UpdateResult(UpdateStatus.Unavailable, Describe());
        try
        {
            if (HasPending) return new UpdateResult(UpdateStatus.Downloaded, $"FontA {PendingVersion} is ready to install.", PendingVersion);

            var info = await _manager!.CheckForUpdatesAsync();
            if (info == null) return new UpdateResult(UpdateStatus.UpToDate, $"FontA {InstalledVersion} is up to date.");

            await _manager.DownloadUpdatesAsync(info, progress, ct);
            _downloaded = info.TargetFullRelease;
            return new UpdateResult(UpdateStatus.Downloaded, $"FontA {info.TargetFullRelease.Version} is ready to install.", info.TargetFullRelease.Version?.ToString());
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult(UpdateStatus.Failed, "The update check was cancelled.");
        }
        catch (Exception ex)
        {
            // Network down, GitHub rate limit, a release without packages... all just "couldn't check", never a crash.
            return new UpdateResult(UpdateStatus.Failed, "Couldn't check for updates: " + ex.Message);
        }
    }

    /// <summary>
    /// Arranges for the downloaded update to be installed once this process has exited. Safe to call at shutdown:
    /// it only starts a small helper that waits for FontA to close.
    /// </summary>
    public void ApplyWhenExited(bool restart)
    {
        var asset = _downloaded ?? _manager?.UpdatePendingRestart;
        if (asset == null || _manager == null) return;
        try { _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: restart); }
        catch (Exception) { /* the update is still downloaded; it will be offered again next time */ }
    }
}
