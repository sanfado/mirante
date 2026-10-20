using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using Mirante.Core.Cameras;
using Mirante.Core.Recording;
using Mirante.Core.Security;

namespace Mirante.App.Video;

internal enum RecordingState
{
    Off,
    Starting,
    Recording,
    Retrying,
}

/// <summary>
/// Records one camera to MKV files over its own connection, independent of what is on screen: it
/// keeps going in the tray and when the camera is left out of a layout, and copies the stream as
/// it comes (no decoding). Continuous recording starts a new file every hour; when the camera
/// accepts another connection the new file starts before the old one closes (no gap), otherwise
/// the switch costs about two seconds. Used from the UI thread.
/// </summary>
internal sealed partial class CameraRecorder : ObservableObject, IDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>No new data for this long: the camera is gone even if the player didn't notice.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a new file may take to get its first data during a switch.</summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A file this small never got video (just the header): deleted instead of kept.</summary>
    private const long EmptyFileBytes = 4096;

    private readonly VideoEngine _engine;
    private readonly ICredentialStore _credentials;
    private readonly Func<CameraConfig> _config;
    private readonly Func<string> _folder;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _watchdog;
    private Session? _session;
    private CancellationTokenSource? _retry;
    private int _attempt;
    private bool _continuous;
    private bool _blocked;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private RecordingState _state;

    /// <summary>Why recording isn't happening right now (shown on the camera), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWanted), nameof(Badge))]
    private bool _isManual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private DateTime? _manualSince;

    public CameraRecorder(VideoEngine engine, ICredentialStore credentials, Func<CameraConfig> config,
        Func<string> folder, Dispatcher dispatcher)
    {
        _engine = engine;
        _credentials = credentials;
        _config = config;
        _folder = folder;
        _dispatcher = dispatcher;
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnWatchdog, dispatcher);
        _watchdog.Stop();
    }

    public bool IsContinuous => _continuous;

    /// <summary>The user wants this camera recorded (manually or continuously).</summary>
    public bool IsWanted => IsManual || _continuous;

    /// <summary>The file being written, if any (never deleted by the cleanup).</summary>
    public string? CurrentFile => _session?.Path;

    public bool HasBadge => Badge is not null;

    /// <summary>Wanted but not recording right now (badge turns amber, with the reason as tooltip).</summary>
    public bool HasProblem => IsWanted && (Problem is not null || State == RecordingState.Retrying);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Badge))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasBadge)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasProblem)));
        }
    }

    /// <summary>Text for the camera's REC badge, or null when not recording.</summary>
    public string? Badge => !IsWanted ? null
        : Problem is not null || State == RecordingState.Retrying ? "REC ⚠"
        : ManualSince is { } since ? $"● REC {Elapsed(DateTime.Now - since)}"
        : "● REC";

    private RecordingKind? Desired => _disposed || _blocked ? null
        : IsManual ? RecordingKind.Manual
        : _continuous ? RecordingKind.Continuous
        : null;

    public void SetContinuous(bool on)
    {
        _continuous = on;
        OnPropertyChanged(nameof(IsContinuous));
        OnPropertyChanged(nameof(IsWanted));
        OnPropertyChanged(nameof(Badge));
        _ = ReconcileAsync();
    }

    /// <summary>"Gravar agora" / stop. With continuous on, it marks the stretch as a kept recording.</summary>
    public void ToggleManual()
    {
        IsManual = !IsManual;
        ManualSince = IsManual ? DateTime.Now : null;
        _ = ReconcileAsync();
    }

    /// <summary>Low disk space or an unusable folder: stop, and start again once it's solved.</summary>
    public void SetBlocked(bool blocked, string? reason)
    {
        if (_blocked == blocked)
        {
            return;
        }

        _blocked = blocked;
        Problem = blocked ? reason : null;
        _ = ReconcileAsync();
    }

    /// <summary>New hour: continuous recordings move to a new file.</summary>
    public void RotateIfDue(DateTime now)
    {
        if (_session is { Kind: RecordingKind.Continuous } session && _gate.CurrentCount > 0
            && now >= session.StartedLocal.Date.AddHours(session.StartedLocal.Hour + 1))
        {
            _ = SwitchAsync(RecordingKind.Continuous);
        }
    }

    /// <summary>The camera's address or password changed: reconnect with the new ones.</summary>
    public void Restart()
    {
        if (_session is { } session)
        {
            _ = SwitchAsync(session.Kind);
        }
    }

    /// <summary>
    /// App closing: closes the file properly (so it gets its index). Runs off the UI thread on
    /// purpose: the app waits for it while blocking the UI thread.
    /// </summary>
    public Task ShutdownAsync()
    {
        _disposed = true;
        _watchdog.Stop();
        CancelRetry();
        var session = _session;
        _session = null;
        return session is null ? Task.CompletedTask : Task.Run(() => StopNow(session));
    }

    public void Dispose() => _ = ShutdownAsync();

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var desired = Desired;
            if (desired is null)
            {
                CancelRetry();
                var old = _session;
                _session = null;
                State = RecordingState.Off;
                if (!_blocked)
                {
                    Problem = null;
                }

                if (old is not null)
                {
                    await StopAsync(old);
                }
            }
            else if (_session is null)
            {
                if (_retry is null)
                {
                    await StartAsync(desired.Value);
                }
            }
            else if (_session.Kind != desired)
            {
                await HandOverAsync(desired.Value);
            }
        }
        finally
        {
            _gate.Release();
        }

        UpdateWatchdog();
    }

    private async Task SwitchAsync(RecordingKind kind)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null && Desired == kind)
            {
                await HandOverAsync(kind);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAsync(RecordingKind kind)
    {
        State = State == RecordingState.Retrying ? RecordingState.Retrying : RecordingState.Starting;
        _session = await TryOpenAsync(kind);
        if (_session is null)
        {
            ScheduleRetry();
        }
    }

    /// <summary>
    /// Moves to a new file. The new one opens first and the old one keeps recording until the
    /// new one gets data; if the camera refuses the extra connection, close first, then open.
    /// </summary>
    private async Task HandOverAsync(RecordingKind kind)
    {
        var old = _session!;
        var next = await TryOpenAsync(kind);
        if (next is not null && await HasDataAsync(next, HandoverTimeout))
        {
            _session = next;
            await StopAsync(old);
            return;
        }

        if (next is not null)
        {
            await StopAsync(next);
        }

        _session = null;
        await StopAsync(old);
        await StartAsync(kind);
    }

    private async Task<Session?> TryOpenAsync(RecordingKind kind)
    {
        var config = _config();
        if (_credentials.Read(config.Id) is not { } credential)
        {
            Problem = "Falta a senha desta câmera";
            return null;
        }

        var folder = _folder();
        if (!RecordingFiles.IsUsableFolder(folder))
        {
            Problem = "A pasta de gravação não pode ser usada";
            return null;
        }

        var started = DateTime.Now;
        var path = RecordingFiles.NextPath(folder, config.Name, started, kind);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "Não foi possível criar a pasta de gravação";
            return null;
        }

        var media = _engine.CreateRecordingMedia(config, credential, path);
        var player = new MediaPlayer(media); // no view: nothing is shown or played, only written
        var session = new Session(player, media, path, kind, started);
        player.EncounteredError += (_, _) => OnFailed(session);
        player.EndReached += (_, _) => OnFailed(session);
        await Task.Run(() => player.Play());
        return session;
    }

    private static async Task<bool> HasDataAsync(Session session, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !session.Failed)
        {
            if (SizeOf(session.Path) > EmptyFileBytes)
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    // LibVLC raises events on its own threads; only flag here, act on the UI thread.
    private void OnFailed(Session session)
    {
        session.Failed = true;
        if (!_disposed && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(() => _ = RecoverAsync(session));
        }
    }

    private async Task RecoverAsync(Session session)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_session, session))
            {
                return; // already replaced (a switch, a stop)
            }

            _session = null;
            Problem = "Sem conexão com a câmera; tentando de novo";
            await StopAsync(session);
            ScheduleRetry();
        }
        finally
        {
            _gate.Release();
        }

        UpdateWatchdog();
    }

    private void ScheduleRetry()
    {
        if (Desired is null || _retry is not null)
        {
            return;
        }

        State = RecordingState.Retrying;
        var delay = Backoff[Math.Min(_attempt, Backoff.Length - 1)];
        _attempt++;
        var retry = _retry = new CancellationTokenSource();
        _ = Task.Delay(delay, retry.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_retry, retry))
                    {
                        _retry = null;
                        retry.Dispose();
                        _ = ReconcileAsync();
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    private void CancelRetry()
    {
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
    }

    private void UpdateWatchdog()
    {
        if (_session is not null || ManualSince is not null)
        {
            _watchdog.Start();
        }
        else
        {
            _watchdog.Stop();
        }
    }

    private void OnWatchdog(object? sender, EventArgs e)
    {
        if (ManualSince is not null)
        {
            OnPropertyChanged(nameof(Badge)); // the timer on the badge
        }

        if (_session is not { } session || _gate.CurrentCount == 0)
        {
            return;
        }

        var size = SizeOf(session.Path);
        if (size > session.LastSize)
        {
            session.LastSize = size;
            session.LastGrowth = DateTime.UtcNow;
            if (size > EmptyFileBytes && State != RecordingState.Recording)
            {
                State = RecordingState.Recording;
                Problem = null;
                _attempt = 0;
            }
        }
        else if (DateTime.UtcNow - session.LastGrowth > StallTimeout)
        {
            OnFailed(session);
        }
    }

    private static async Task StopAsync(Session session) => await Task.Run(() => StopNow(session));

    private static void StopNow(Session session)
    {
        try
        {
            session.Player.Stop();
            session.Player.Dispose();
            session.Media.Dispose();
            if (SizeOf(session.Path) <= EmptyFileBytes)
            {
                File.Delete(session.Path); // never got video: don't leave empty files around
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Debug.WriteLine($"Gravação: {ex.Message}");
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Elapsed(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Session(MediaPlayer player, Media media, string path, RecordingKind kind, DateTime startedLocal)
    {
        public MediaPlayer Player { get; } = player;

        public Media Media { get; } = media;

        public string Path { get; } = path;

        public RecordingKind Kind { get; } = kind;

        public DateTime StartedLocal { get; } = startedLocal;

        public long LastSize { get; set; }

        public DateTime LastGrowth { get; set; } = DateTime.UtcNow;

        /// <summary>Set from LibVLC's threads, read on the UI thread.</summary>
        public bool Failed
        {
            get => Volatile.Read(ref _failed);
            set => Volatile.Write(ref _failed, value);
        }

        private bool _failed;
    }
}
