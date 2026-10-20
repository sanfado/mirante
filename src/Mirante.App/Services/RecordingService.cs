using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mirante.App.Video;
using Mirante.Core.Cameras;
using Mirante.Core.Recording;
using Mirante.Core.Storage;

namespace Mirante.App.Services;

/// <summary>
/// Housekeeping for every camera's recorder: where each new file goes (recording locations), a new
/// file each hour, moving to another disk when one fills up or is unplugged, deleting old
/// continuous recordings and telling the user when something fails. Disks are measured in the
/// background (a network folder can take seconds to answer).
/// </summary>
internal sealed class RecordingService : IDisposable
{
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AlertEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MeasureEvery = TimeSpan.FromSeconds(15);

    /// <summary>A recorder must be failing this long before the user is told (camera reboots are short).</summary>
    private static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlyList<(string Name, CameraRecorder Recorder)>> _recorders;
    private readonly SettingsRepository _settings;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<CameraRecorder, DateTime> _failingSince = [];
    private readonly Dictionary<CameraRecorder, DateTime> _alerted = [];
    private readonly HashSet<Guid> _cannotWrite = [];
    private IReadOnlyList<LocationState> _states = [];
    private DateTime _measuredAt;
    private Task<IReadOnlyList<LocationState>>? _measuring;
    private DateTime _nextCleanup = DateTime.UtcNow.AddMinutes(1);
    private bool _cleaning;
    private bool _ticking;

    public RecordingService(Func<IReadOnlyList<(string Name, CameraRecorder Recorder)>> recorders,
        SettingsRepository settings, Dispatcher dispatcher)
    {
        _recorders = recorders;
        _settings = settings;
        _timer = new DispatcherTimer(MeasureEvery, DispatcherPriority.Background, (_, _) => _ = TickAsync(), dispatcher);
    }

    /// <summary>Something the user should know (shown in the app and, from the tray, as a notification).</summary>
    public event Action<string>? Alert;

    /// <summary>The number of cameras being recorded changed (for the tray tooltip).</summary>
    public event Action<int>? ActiveChanged;

    public int ActiveCount { get; private set; }

    /// <summary>The recording locations, in order of preference (never empty).</summary>
    public IReadOnlyList<RecordingLocation> Locations =>
        RecordingStorage.Normalize(_settings.Load().RecordingLocations, AppPaths.DefaultRecordingFolder);

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    /// <summary>The folder for a camera's next file; null when no disk can take it.</summary>
    public async Task<string?> FolderForAsync(CameraConfig camera)
    {
        var locations = Locations;
        var states = await StatesAsync(locations);
        var fixedLocation = camera.RecordingLocation is { } id && locations.Any(l => l.Id == id) ? id : (Guid?)null;
        return RecordingStorage.Choose(states, fixedLocation)?.Folder;
    }

    /// <summary>Measures every location now (for the settings window).</summary>
    public static Task<IReadOnlyList<LocationState>> MeasureAsync(IReadOnlyList<RecordingLocation> locations) =>
        Task.Run(() => Measure(locations));

    /// <summary>Runs the checks now (e.g. right after the settings changed).</summary>
    public void Refresh()
    {
        _measuredAt = default;
        _ = TickAsync();
    }

    private async Task<IReadOnlyList<LocationState>> StatesAsync(IReadOnlyList<RecordingLocation> locations)
    {
        var fresh = DateTime.UtcNow - _measuredAt < MeasureEvery
                    && _states.Select(s => s.Location).SequenceEqual(locations);
        if (fresh)
        {
            return _states;
        }

        // One measurement at a time; recorders starting together share it.
        _measuring ??= MeasureAsync(locations);
        try
        {
            _states = await _measuring;
            _measuredAt = DateTime.UtcNow;
        }
        finally
        {
            _measuring = null;
        }

        return _states;
    }

    private async Task TickAsync()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        try
        {
            var locations = Locations;
            _measuredAt = default;
            var states = await StatesAsync(locations);
            var recorders = _recorders();
            ReportLocations(states, recorders.Any(r => r.Recorder.IsWanted));

            var now = DateTime.Now;
            foreach (var (name, recorder) in recorders)
            {
                recorder.RotateIfDue(now);
                if (recorder.CurrentFile is { } file && ShouldMove(recorder, file, locations, states))
                {
                    recorder.Restart(); // the next file goes wherever there is room now
                }

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
                var days = RecordingFiles.ClampRetentionDays(_settings.Load().RecordingRetentionDays);
                var inUse = recorders.Select(r => r.Recorder.CurrentFile).OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _cleaning = true;
                try
                {
                    await Task.Run(() => Cleanup(locations, days, inUse));
                }
                finally
                {
                    _cleaning = false;
                }
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>
    /// The file being written is on a disk that can't take more (full, unplugged, location removed),
    /// or a camera fixed to a location is writing elsewhere and that location is back.
    /// </summary>
    private static bool ShouldMove(CameraRecorder recorder, string file, IReadOnlyList<RecordingLocation> locations,
        IReadOnlyList<LocationState> states)
    {
        var current = RecordingStorage.LocationOf(locations, file);
        if (current is null || states.FirstOrDefault(s => s.Location.Id == current.Id) is not { CanWrite: true })
        {
            return true;
        }

        return recorder.FixedLocation is { } wanted && wanted != current.Id
               && states.FirstOrDefault(s => s.Location.Id == wanted) is { CanWrite: true };
    }

    private void ReportLocations(IReadOnlyList<LocationState> states, bool recording)
    {
        foreach (var state in states)
        {
            var changed = state.CanWrite ? _cannotWrite.Remove(state.Location.Id) : _cannotWrite.Add(state.Location.Id);
            if (!changed || !recording)
            {
                continue;
            }

            var others = states.Any(s => s.CanWrite && s.Location.Id != state.Location.Id);
            Alert?.Invoke(state.CanWrite ? $"O local de gravação {state.Location.Folder} está disponível de novo"
                : !state.Available ? $"O local de gravação {state.Location.Folder} não está acessível (disco desconectado?){(others ? "; gravando em outro local" : "; gravação parada")}"
                : $"O local de gravação {state.Location.Folder} tem menos de 5 GB livres{(others ? "; gravando em outro local" : "; gravação parada")}");
        }
    }

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

    private static IReadOnlyList<LocationState> Measure(IReadOnlyList<RecordingLocation> locations) =>
        [.. locations.Select(location =>
        {
            var root = Path.GetPathRoot(location.Folder);
            var available = !string.IsNullOrEmpty(root) && Directory.Exists(root);
            if (!available)
            {
                return new LocationState(location, false, 0, 0, null);
            }

            var files = RecordingFiles.Scan(location.Folder);
            var oldest = files.Where(f => f.Kind == RecordingKind.Continuous).Select(f => (DateTime?)f.LastWriteUtc).Min();
            return new LocationState(location, true, FreeBytes(location.Folder) ?? 0, files.Sum(f => f.Size), oldest);
        })];

    /// <summary>
    /// Per location: continuous recordings older than the retention, then the oldest ones until it
    /// fits its limit. Only Mirante's own files, never manual ones or files being written.
    /// </summary>
    private static void Cleanup(IReadOnlyList<RecordingLocation> locations, int retentionDays, IReadOnlySet<string> inUse)
    {
        foreach (var location in locations)
        {
            try
            {
                var files = RecordingFiles.Scan(location.Folder);
                foreach (var file in RecordingFiles.SelectForCleanup(files, DateTime.UtcNow, retentionDays, location.MaxBytes, inUse))
                {
                    Delete(file.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Limpeza: {ex.Message}");
            }
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
            var day = Path.GetDirectoryName(path)!;
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
