using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Mirante.Core.Cameras;
using Mirante.Core.Rtsp;
using Mirante.Core.Security;

namespace Mirante.App.Video;

internal enum StreamStatus
{
    Idle,
    Connecting,
    Live,
    Reconnecting,
    Offline,
    AuthFailed,
    MissingCredentials,
}

/// <summary>
/// Owns the player of one camera: starts it on whatever view currently shows it,
/// detects stalls, reconnects with backoff and diagnoses why a connection failed.
/// State changes happen on the UI thread; blocking LibVLC calls run in the background.
/// </summary>
internal sealed partial class CameraStream : ObservableObject, IDisposable
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly VideoEngine _engine;
    private readonly ICredentialStore _credentials;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _playerGate = new(1, 1);
    private readonly DispatcherTimer _watchdog;

    private VideoView? _view;
    private Media? _media;
    private CancellationTokenSource? _retry;
    private long _lastDecoded;
    private DateTime _lastProgress;
    private DateTime _startedAt;
    private int _attempt;
    private bool _paused;
    private bool _disposed;

    [ObservableProperty]
    private StreamStatus _status = StreamStatus.Idle;

    public CameraStream(CameraConfig config, VideoEngine engine, ICredentialStore credentials, Dispatcher dispatcher)
    {
        Config = config;
        _engine = engine;
        _credentials = credentials;
        _dispatcher = dispatcher;
        Player = new MediaPlayer(engine.LibVlc) { EnableHardwareDecoding = true, Mute = true };
        Player.Vout += OnVout;
        Player.EncounteredError += OnPlayerFailed;
        Player.EndReached += OnPlayerFailed;
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnWatchdog, dispatcher);
    }

    public CameraConfig Config { get; private set; }

    public MediaPlayer Player { get; }

    public bool IsLive => Status == StreamStatus.Live;

    /// <summary>Shows the stream on <paramref name="view"/>, moving it from any previous view.</summary>
    public void Attach(VideoView view)
    {
        if (_disposed || ReferenceEquals(_view, view))
        {
            return;
        }

        if (_view is not null)
        {
            _view.MediaPlayer = null;
        }

        _view = view;
        view.MediaPlayer = Player;
        _attempt = 0;
        _ = RestartAsync();
    }

    /// <summary>Stops the stream if it is currently shown on <paramref name="view"/>.</summary>
    public void Release(VideoView view)
    {
        if (!ReferenceEquals(_view, view))
        {
            return;
        }

        view.MediaPlayer = null;
        _view = null;
        CancelRetry();
        _watchdog.Stop();
        Status = StreamStatus.Idle;
        _ = RunPlayerAsync(() => Player.Stop());
    }

    public void UpdateConfig(CameraConfig config)
    {
        Config = config;
        Retry();
    }

    /// <summary>Retries now (e.g. after the user fixed the password).</summary>
    public void Retry()
    {
        _attempt = 0;
        _ = RestartAsync();
    }

    /// <summary>Stops decoding while the app sits in the tray; <see cref="Resume"/> restarts it.</summary>
    public void Pause()
    {
        _paused = true;
        CancelRetry();
        _watchdog.Stop();
        Status = StreamStatus.Idle;
        _ = RunPlayerAsync(() => Player.Stop());
    }

    public void Resume()
    {
        if (_paused)
        {
            _paused = false;
            Retry();
        }
    }

    public bool TakeSnapshot(string path)
    {
        if (!IsLive)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Player.TakeSnapshot(0, path, 0, 0);
    }

    partial void OnStatusChanged(StreamStatus value) => OnPropertyChanged(nameof(IsLive));

    private async Task RestartAsync()
    {
        CancelRetry();
        if (_disposed || _paused || _view is null)
        {
            return;
        }

        var credential = _credentials.Read(Config.Id);
        if (credential is null)
        {
            _watchdog.Stop();
            Status = StreamStatus.MissingCredentials;
            await RunPlayerAsync(() => Player.Stop()).ConfigureAwait(true);
            return;
        }

        Status = _attempt == 0 ? StreamStatus.Connecting : StreamStatus.Reconnecting;
        _startedAt = _lastProgress = DateTime.UtcNow;
        _lastDecoded = 0;

        var media = _engine.CreateMedia(Config, credential);
        var previous = _media;
        _media = media;
        await RunPlayerAsync(() =>
        {
            Player.Stop();
            Player.Play(media);
        }).ConfigureAwait(true);
        previous?.Dispose();
        _watchdog.Start();
    }

    private void ScheduleReconnect()
    {
        if (_disposed || _paused || _view is null || _retry is not null
            || Status is StreamStatus.AuthFailed or StreamStatus.MissingCredentials)
        {
            return;
        }

        _watchdog.Stop();
        var delay = Backoff[Math.Min(_attempt, Backoff.Length - 1)];
        _attempt++;
        Status = StreamStatus.Reconnecting;
        var retry = _retry = new CancellationTokenSource();
        var config = Config;
        _ = Task.Run(async () =>
        {
            try
            {
                // Ask the camera directly why it failed: wrong password must stop the retries.
                var probe = await RtspProbe.ProbeAsync(RtspAddress.Build(config), _credentials.Read(config.Id),
                    ProbeTimeout, retry.Token).ConfigureAwait(false);
                var diagnosis = probe.Status switch
                {
                    RtspProbeStatus.AuthenticationFailed => StreamStatus.AuthFailed,
                    RtspProbeStatus.Unreachable or RtspProbeStatus.Timeout => StreamStatus.Offline,
                    _ => StreamStatus.Reconnecting,
                };
                OnUi(() =>
                {
                    if (!retry.IsCancellationRequested)
                    {
                        Status = diagnosis;
                    }
                });
                if (diagnosis == StreamStatus.AuthFailed)
                {
                    await RunPlayerAsync(() => Player.Stop()).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(delay, retry.Token).ConfigureAwait(false);
                OnUi(() =>
                {
                    if (ReferenceEquals(_retry, retry))
                    {
                        _retry = null;
                        _ = RestartAsync();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer attempt
            }
        });
    }

    private void OnWatchdog(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (Status == StreamStatus.Live)
        {
            var decoded = _media?.Statistics.DecodedVideo ?? 0;
            if (decoded != _lastDecoded)
            {
                _lastDecoded = decoded;
                _lastProgress = now;
            }
            else if (now - _lastProgress > StallTimeout)
            {
                ScheduleReconnect();
            }
        }
        else if (Status is StreamStatus.Connecting or StreamStatus.Reconnecting && now - _startedAt > ConnectTimeout)
        {
            ScheduleReconnect();
        }
    }

    // LibVLC raises events on its own threads; never call back into the player from them.
    private void OnVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        if (e.Count > 0)
        {
            OnUi(() =>
            {
                Status = StreamStatus.Live;
                _attempt = 0;
                _lastProgress = DateTime.UtcNow;
            });
        }
    }

    private void OnPlayerFailed(object? sender, EventArgs e) => OnUi(ScheduleReconnect);

    private void OnUi(Action action)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private async Task RunPlayerAsync(Action action)
    {
        await _playerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(action).ConfigureAwait(false);
        }
        finally
        {
            _playerGate.Release();
        }
    }

    private void CancelRetry()
    {
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
    }

    /// <summary>Stops playback in the background; LibVLC's Stop can block for seconds on RTSP.</summary>
    public Task ShutdownAsync()
    {
        _disposed = true;
        CancelRetry();
        _watchdog.Stop();
        Player.Vout -= OnVout;
        Player.EncounteredError -= OnPlayerFailed;
        Player.EndReached -= OnPlayerFailed;
        return Task.Run(() =>
        {
            Player.Stop();
            Player.Dispose();
            _media?.Dispose();
        });
    }

    public void Dispose()
    {
        _ = ShutdownAsync();
        _playerGate.Dispose();
    }
}
