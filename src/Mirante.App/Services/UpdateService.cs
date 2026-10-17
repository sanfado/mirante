using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Velopack;
using Velopack.Sources;

namespace Mirante.App.Services;

internal enum UpdateCheckResult
{
    NotInstalled,
    UpToDate,
    Ready,
    Failed,
}

/// <summary>
/// Looks for new releases on GitHub and downloads them in the background, but never applies
/// one on its own: the user decides when to restart into it.
/// Integrity: Velopack checks every package against the hashes in the release feed, and
/// everything is fetched over HTTPS from this repository's releases only.
/// </summary>
internal sealed partial class UpdateService : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/sanfado/mirante";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager;
    private readonly DispatcherTimer _timer;
    private VelopackAsset? _ready;
    private bool _checking;

    /// <summary>Version already downloaded and waiting for the user's go-ahead, if any.</summary>
    [ObservableProperty]
    private string? _readyVersion;

    public UpdateService(Dispatcher dispatcher)
    {
        IUpdateSource source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: false);
#if DEBUG
        // Debug builds only: test the whole update flow against a local folder of releases.
        if (Environment.GetEnvironmentVariable("MIRANTE_UPDATE_SOURCE") is { Length: > 0 } folder)
        {
            source = new SimpleFileSource(new DirectoryInfo(folder));
        }
#endif
        _manager = new UpdateManager(source);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FirstCheckDelay };

        // Downloaded in a previous session but not applied yet: offer it right away.
        if (IsSupported && _manager.UpdatePendingRestart is { } pending)
        {
            _ready = pending;
            ReadyVersion = pending.Version.ToString();
        }
    }

    /// <summary>False when running from a build folder instead of an installation.</summary>
    public bool IsSupported => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppInfo.Version;

    public string? LastError { get; private set; }

    /// <summary>Checks shortly after startup and then every few hours, while <paramref name="enabled"/> says so.</summary>
    public void StartBackgroundChecks(Func<bool> enabled)
    {
        if (!IsSupported)
        {
            return;
        }

        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (enabled())
            {
                await CheckAsync();
            }
        };
        _timer.Start();
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsSupported)
        {
            return UpdateCheckResult.NotInstalled;
        }

        if (_ready is not null || _checking)
        {
            return _ready is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Ready;
        }

        _checking = true;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                LastError = null;
                return UpdateCheckResult.UpToDate;
            }

            await _manager.DownloadUpdatesAsync(info);
            _ready = info.TargetFullRelease;
            ReadyVersion = _ready.Version.ToString();
            LastError = null;
            return UpdateCheckResult.Ready;
        }
#pragma warning disable CA1031 // An update check must never take the app down (offline, GitHub down, private repo…).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LastError = ex.Message;
            return UpdateCheckResult.Failed;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_ready is not null)
        {
            _manager.ApplyUpdatesAndRestart(_ready);
        }
    }
}
