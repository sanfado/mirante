using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mirante.App.Video;
using Mirante.Core.Recording;
using Mirante.Core.Storage;

namespace Mirante.App.Services;

/// <summary>
/// Housekeeping for every camera's recorder: a new file each hour, stopping (and resuming) when the
/// disk runs low, deleting old continuous recordings and telling the user when something fails.
/// </summary>
internal sealed class RecordingService : IDisposable
{
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AlertEvery = TimeSpan.FromMinutes(10);

    /// <summary>A recorder must be failing this long before the user is told (camera reboots are short).</summary>
    private static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlyList<(string Name, CameraRecorder Recorder)>> _recorders;
    private readonly SettingsRepository _settings;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<CameraRecorder, DateTime> _failingSince = [];
    private readonly Dictionary<CameraRecorder, DateTime> _alerted = [];
    private DateTime _nextCleanup = DateTime.UtcNow.AddMinutes(1);
    private bool _cleaning;
    private bool _lowDisk;

    public RecordingService(Func<IReadOnlyList<(string Name, CameraRecorder Recorder)>> recorders,
        SettingsRepository settings, Dispatcher dispatcher)
    {
        _recorders = recorders;
        _settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    /// <summary>Something the user should know (shown in the app and, from the tray, as a notification).</summary>
    public event Action<string>? Alert;

    /// <summary>The number of cameras being recorded changed (for the tray tooltip).</summary>
    public event Action<int>? ActiveChanged;

    public int ActiveCount { get; private set; }

    public string Folder => Resolve(_settings.Load());

    public static string Resolve(AppSettings settings) =>
        settings.RecordingFolder is { } folder && RecordingFiles.IsUsableFolder(folder) ? folder : AppPaths.DefaultRecordingFolder;

    public void Start() => _timer.Start();

    /// <summary>Runs the checks now (e.g. right after recording was turned on).</summary>
    public void Tick()
    {
        var recorders = _recorders();
        var folder = Folder;

        // Low disk: stop everything and start again once there is room.
        var low = recorders.Any(r => r.Recorder.IsWanted) && FreeBytes(folder) is { } free && free < RecordingFiles.MinimumFreeBytes;
        if (low != _lowDisk)
        {
            _lowDisk = low;
            foreach (var (_, recorder) in recorders)
            {
                recorder.SetBlocked(low, "Pouco espaço em disco (menos de 5 GB livres)");
            }

            Alert?.Invoke(low ? "Gravação parada: menos de 5 GB livres no disco das gravações"
                : "Há espaço em disco de novo; a gravação voltou");
        }

        var now = DateTime.Now;
        foreach (var (name, recorder) in recorders)
        {
            recorder.RotateIfDue(now);
            WatchForFailures(name, recorder);
        }

        var active = recorders.Count(r => r.Recorder.State == RecordingState.Recording);
        if (active != ActiveCount)
        {
            ActiveCount = active;
            ActiveChanged?.Invoke(active);
        }

        if (DateTime.UtcNow >= _nextCleanup && !_cleaning)
        {
            _nextCleanup = DateTime.UtcNow + CleanupEvery;
            var settings = _settings.Load();
            var inUse = recorders.Select(r => r.Recorder.CurrentFile).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cleaning = true;
            _ = Task.Run(() => Cleanup(folder, RecordingFiles.ClampRetentionDays(settings.RecordingRetentionDays),
                    RecordingFiles.ClampMaxGigabytes(settings.RecordingMaxGigabytes) * 1024L * 1024 * 1024, inUse))
                .ContinueWith(_ => _cleaning = false, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    public void Dispose() => _timer.Stop();

    private void WatchForFailures(string name, CameraRecorder recorder)
    {
        if (!recorder.IsWanted || recorder.Problem is null)
        {
            _failingSince.Remove(recorder);
            return;
        }

        var now = DateTime.UtcNow;
        if (!_failingSince.TryGetValue(recorder, out var since))
        {
            _failingSince[recorder] = now;
            return;
        }

        if (now - since >= AlertAfter && (!_alerted.TryGetValue(recorder, out var last) || now - last >= AlertEvery))
        {
            _alerted[recorder] = now;
            Alert?.Invoke($"Gravação de {name}: {recorder.Problem}");
        }
    }

    /// <summary>Deletes what the limits say (only Mirante's own files) and the folders left empty.</summary>
    private static void Cleanup(string folder, int retentionDays, long maxBytes, IReadOnlySet<string> inUse)
    {
        try
        {
            var files = RecordingFiles.Scan(folder);
            foreach (var file in RecordingFiles.SelectForCleanup(files, DateTime.UtcNow, retentionDays, maxBytes, inUse))
            {
                try
                {
                    File.Delete(file.Path);
                    var day = Path.GetDirectoryName(file.Path)!;
                    if (!Directory.EnumerateFileSystemEntries(day).Any())
                    {
                        Directory.Delete(day);
                        var camera = Path.GetDirectoryName(day)!;
                        if (!Directory.EnumerateFileSystemEntries(camera).Any())
                        {
                            Directory.Delete(camera);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Limpeza: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Limpeza: {ex.Message}");
        }
    }

    /// <summary>Free space where recordings go (works for network folders too); null if unknown.</summary>
    private static long? FreeBytes(string folder)
    {
        var path = folder;
        while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path);
        }

        return !string.IsNullOrEmpty(path) && NativeMethods.GetDiskFreeSpaceExW(path, out var free, out _, out _) ? (long)free : null;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeForUser, out ulong total, out ulong totalFree);
    }
}
