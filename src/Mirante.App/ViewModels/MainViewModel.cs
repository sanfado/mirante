using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirante.App.Video;
using Mirante.Core.Cameras;
using Mirante.Core.Layout;
using Mirante.Core.Security;
using Mirante.Core.Storage;

namespace Mirante.App.ViewModels;

/// <summary>What the view model needs from the window (dialogs, windows, fullscreen).</summary>
internal interface IShell
{
    Task<CameraEditResult?> EditCameraAsync(CameraConfig? existing);

    Task<bool> ConfirmRemoveAsync(string cameraName);

    /// <summary>Name for a new saved position, or null if cancelled.</summary>
    Task<string?> AskPositionNameAsync(string cameraName, string suggestion);

    /// <summary>Lets the user reorder the cameras; returns the new order or null if cancelled.</summary>
    IReadOnlyList<Guid>? OrganizeCameras(IReadOnlyList<(Guid Id, string Name)> cameras);

    void OpenDetached(CameraTileViewModel tile);

    void CloseDetached(CameraTileViewModel tile);

    void EnterFullscreen();

    void ShowToast(string message, string? openFolder = null);
}

/// <param name="Config">Camera settings.</param>
/// <param name="Credential">New credential, or null to keep the stored one.</param>
internal sealed record CameraEditResult(CameraConfig Config, CameraCredential? Credential);

internal sealed partial class MainViewModel : ObservableObject
{
    private readonly CameraRepository _cameras;
    private readonly ICredentialStore _credentials;
    private readonly VideoEngine _engine;
    private readonly Dispatcher _dispatcher;
    private double _stageWidth;
    private double _stageHeight;

    [ObservableProperty]
    private int _rows = 1;

    [ObservableProperty]
    private int _columns = 1;

    [ObservableProperty]
    private CameraTileViewModel? _focusedTile;

    /// <summary>The only camera whose sound plays (null = all muted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioTile))]
    private CameraTileViewModel? _audioTile;

    /// <summary>Volume (0–200%) of the camera that is playing; saved per camera.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmplified))]
    private int _volume = AudioBoost.DefaultVolume;

    /// <summary>"Realçar voz" of the camera that is playing; saved per camera.</summary>
    [ObservableProperty]
    private bool _voiceBoost;

    private readonly DispatcherTimer _saveSoon;
    private bool _syncingAudio;
    private int _defaultVolume = AudioBoost.DefaultVolume;

    public MainViewModel(CameraRepository cameras, ICredentialStore credentials, VideoEngine engine,
        Func<string> snapshotFolder, Func<string> recordingFolder, Dispatcher dispatcher)
    {
        _cameras = cameras;
        _credentials = credentials;
        _engine = engine;
        _dispatcher = dispatcher;
        SnapshotFolder = snapshotFolder;
        RecordingFolder = recordingFolder;
        _saveSoon = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveSoon.Tick += (_, _) =>
        {
            _saveSoon.Stop();
            Save();
        };
        foreach (var config in cameras.Load())
        {
            Tiles.Add(CreateTile(config));
        }

        Tiles.CollectionChanged += (_, _) => OnTilesChanged();
        OnTilesChanged();
    }

    public IShell? Shell { get; set; }

    public Func<string> SnapshotFolder { get; set; }

    public Func<string> RecordingFolder { get; set; }

    /// <summary>Every camera's recorder, with its name (for the recording service).</summary>
    public IReadOnlyList<(string Name, CameraRecorder Recorder)> Recorders => [.. Tiles.Select(t => (t.Name, t.Recorder))];

    /// <summary>"Gravar agora" / stop.</summary>
    public void ToggleRecording(CameraTileViewModel tile)
    {
        tile.Recorder.ToggleManual();
        Shell?.ShowToast(tile.Recorder.IsManual ? $"Gravando {tile.Name}" : $"Gravação de {tile.Name} salva", tile.Recorder.IsManual ? null : RecordingFolder());
    }

    public void SetContinuousRecording(CameraTileViewModel tile, bool on)
    {
        tile.Stream.UpdateContinuousRecording(on);
        tile.Recorder.SetContinuous(on);
        Save();
        Shell?.ShowToast(on ? $"Gravação contínua ligada em {tile.Name}" : $"Gravação contínua desligada em {tile.Name}");
    }

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = [];

    public ObservableCollection<CameraTileViewModel> DetachedTiles { get; } = [];

    public bool HasCameras => Tiles.Count > 0;

    public bool HasAudioTile => AudioTile is not null;

    /// <summary>Above 100%: the app is amplifying (shown in a warning color).</summary>
    public bool IsAmplified => Volume > 100;

    /// <summary>Volume for cameras without their own (from older settings).</summary>
    public int DefaultVolume
    {
        get => _defaultVolume;
        set
        {
            _defaultVolume = AudioBoost.Clamp(value);
            foreach (var tile in Tiles)
            {
                tile.Stream.DefaultVolume = _defaultVolume;
            }
        }
    }

    /// <summary>Turns this camera's sound on (muting any other) or off.</summary>
    public void ToggleAudio(CameraTileViewModel tile) =>
        AudioTile = ReferenceEquals(AudioTile, tile) ? null : tile;

    partial void OnAudioTileChanged(CameraTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.Stream.SetAudio(ReferenceEquals(tile, value));
            tile.RefreshAudio();
        }

        // Show the controls with this camera's own settings.
        if (value is not null)
        {
            _syncingAudio = true;
            Volume = value.Stream.Volume;
            VoiceBoost = value.Stream.Config.VoiceBoost;
            _syncingAudio = false;
        }
    }

    partial void OnVolumeChanged(int value) =>
        UpdateAudioTile(c => c with { Volume = AudioBoost.Clamp(value) });

    partial void OnVoiceBoostChanged(bool value) => UpdateAudioTile(c => c with { VoiceBoost = value });

    private void UpdateAudioTile(Func<CameraConfig, CameraConfig> change)
    {
        if (_syncingAudio || AudioTile is null)
        {
            return;
        }

        AudioTile.Stream.UpdateAudioSettings(change(AudioTile.Stream.Config));
        _saveSoon.Stop(); // dragging the slider: save once it settles
        _saveSoon.Start();
    }

    [RelayCommand]
    private void ToggleVoiceBoost() => VoiceBoost = !VoiceBoost;

    [RelayCommand]
    private void MuteAll() => AudioTile = null;

    [RelayCommand]
    private void Organize()
    {
        if (Shell?.OrganizeCameras([.. Tiles.Select(t => (t.Id, t.Name))]) is { } order)
        {
            ApplyOrder(order);
        }
    }

    /// <summary>Puts the cameras in <paramref name="order"/> (mosaic, keys 1–9) and saves it.</summary>
    public void ApplyOrder(IReadOnlyList<Guid> order)
    {
        for (var target = 0; target < order.Count && target < Tiles.Count; target++)
        {
            var current = Tiles.ToList().FindIndex(t => t.Id == order[target]);
            if (current >= 0 && current != target)
            {
                Tiles.Move(current, target);
            }
        }

        Save();
    }

    public bool AllDetached => Tiles.Any(t => t.IsDetached) && Tiles.All(t => t.IsDetached || t.IsExcluded);

    public int HiddenCount => Tiles.Count(t => t.IsExcluded);

    public bool HasHiddenCameras => HiddenCount > 0;

    public string HiddenLabel => HiddenCount == 1 ? "1 oculta" : $"{HiddenCount} ocultas";

    /// <summary>
    /// Leaves these cameras out of the screen (paused) and brings back all others. Called with the
    /// layout being applied, or with nothing to show every camera again.
    /// </summary>
    public void SetExcluded(IEnumerable<Guid> cameraIds)
    {
        var excluded = cameraIds.ToHashSet();
        foreach (var tile in Tiles)
        {
            var leaveOut = excluded.Contains(tile.Id);
            if (leaveOut && tile.IsDetached)
            {
                Shell?.CloseDetached(tile);
            }

            if (leaveOut && ReferenceEquals(AudioTile, tile))
            {
                AudioTile = null;
            }

            if (leaveOut && ReferenceEquals(FocusedTile, tile))
            {
                FocusedTile = null;
            }

            tile.IsExcluded = leaveOut;
        }

        UpdateGrid();
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHiddenCameras));
        OnPropertyChanged(nameof(HiddenLabel));
    }

    public string Subtitle => Tiles.Count switch
    {
        0 => "Nenhuma câmera",
        1 => "1 câmera",
        var n => $"{n} câmeras",
    };

    public CameraCredential? StoredCredential(Guid cameraId) => _credentials.Read(cameraId);

    public void UpdateStageSize(double width, double height)
    {
        _stageWidth = width;
        _stageHeight = height;
        UpdateGrid();
    }

    [RelayCommand]
    private async Task AddCameraAsync()
    {
        if (Shell is null || await Shell.EditCameraAsync(null) is not { } result)
        {
            return;
        }

        if (result.Credential is not null)
        {
            _credentials.Write(result.Config.Id, result.Credential);
        }

        Tiles.Add(CreateTile(result.Config));
        Save();
    }

    public async Task EditCameraAsync(CameraTileViewModel tile)
    {
        if (Shell is null || await Shell.EditCameraAsync(tile.Stream.Config) is not { } result)
        {
            return;
        }

        if (result.Credential is not null)
        {
            _credentials.Write(result.Config.Id, result.Credential);
        }

        tile.Stream.UpdateConfig(result.Config);
        tile.RefreshConfig();
        tile.Recorder.Restart(); // address or password may have changed
        Save();
    }

    public async Task RemoveCameraAsync(CameraTileViewModel tile)
    {
        if (Shell is null || !await Shell.ConfirmRemoveAsync(tile.Name))
        {
            return;
        }

        RemoveCamera(tile);
    }

    /// <summary>Removes the camera without asking (the confirmation is the caller's job).</summary>
    public void RemoveCamera(CameraTileViewModel tile)
    {
        // Stop the player first, while its view still exists; everything after this
        // (closing its window, removing its tile) can no longer touch the player.
        _ = tile.Stream.ShutdownAsync();
        tile.Ptz.Dispose();
        tile.Recorder.Dispose(); // the camera's recordings stay on disk

        if (tile.IsDetached)
        {
            Shell?.CloseDetached(tile);
        }

        if (ReferenceEquals(FocusedTile, tile))
        {
            FocusedTile = null;
        }

        if (ReferenceEquals(AudioTile, tile))
        {
            AudioTile = null;
        }

        Tiles.Remove(tile);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHiddenCameras));
        OnPropertyChanged(nameof(HiddenLabel));
        _credentials.Delete(tile.Id);
        Save();
    }

    /// <summary>Saves where the camera points now under a name; the same name replaces the old one.</summary>
    public async Task SavePositionAsync(CameraTileViewModel tile)
    {
        var positions = tile.Positions;
        if (positions.Count >= CameraPosition.MaxCount)
        {
            Shell?.ShowToast($"{tile.Name} já tem {CameraPosition.MaxCount} posições; remova uma antes");
            return;
        }

        if (await tile.Ptz.ReadPositionAsync() is not { } current)
        {
            Shell?.ShowToast($"Não foi possível ler a posição de {tile.Name}");
            return;
        }

        if (Shell is null || await Shell.AskPositionNameAsync(tile.Name, $"Posição {positions.Count + 1}") is not { } typed
            || string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        var name = typed.Trim();
        var position = new CameraPosition(name, Math.Clamp(current.Pan, -1, 1), Math.Clamp(current.Tilt, -1, 1),
            current.Zoom is { } zoom ? Math.Clamp(zoom, -1, 1) : null);
        var replaced = positions.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        tile.SetPositions(replaced
            ? [.. positions.Select(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? position : p)]
            : [.. positions, position]);
        Save();
        Shell.ShowToast(replaced ? $"Posição “{name}” atualizada" : $"Posição “{name}” salva");
    }

    public void RemovePosition(CameraTileViewModel tile, CameraPosition position)
    {
        tile.SetPositions([.. tile.Positions.Where(p => p != position)]);
        Save();
    }

    public void TakeSnapshot(CameraTileViewModel tile)
    {
        var folder = SnapshotFolder();
        var path = SnapshotNaming.NextPath(folder, tile.Name, DateTime.Now);
        if (tile.Stream.TakeSnapshot(path))
        {
            Shell?.ShowToast($"Foto de {tile.Name} salva", folder);
        }
        else
        {
            Shell?.ShowToast($"{tile.Name} não está ao vivo agora");
        }
    }

    public void ToggleDetach(CameraTileViewModel tile)
    {
        if (tile.IsDetached)
        {
            Shell?.CloseDetached(tile); // the window calls Reattach when it closes
            return;
        }

        if (ReferenceEquals(FocusedTile, tile))
        {
            FocusedTile = null;
        }

        tile.IsDetached = true;
        DetachedTiles.Add(tile);
        Shell?.OpenDetached(tile);
        UpdateGrid();
    }

    public void Reattach(CameraTileViewModel tile)
    {
        tile.IsDetached = false;
        DetachedTiles.Remove(tile);
        UpdateGrid();
    }

    [RelayCommand]
    private void BringBack(CameraTileViewModel tile) => Shell?.CloseDetached(tile);

    public void ToggleFocus(CameraTileViewModel tile) =>
        FocusedTile = ReferenceEquals(FocusedTile, tile) ? null : tile;

    public void ShowFullscreen(CameraTileViewModel tile)
    {
        FocusedTile = tile;
        Shell?.EnterFullscreen();
    }

    /// <summary>Focus the n-th camera of the mosaic (keyboard 1–9); 0 or out of range clears focus.</summary>
    public void FocusByNumber(int number)
    {
        var candidates = Tiles.Where(t => !t.IsDetached && !t.IsExcluded).ToList();
        FocusedTile = number >= 1 && number <= candidates.Count ? candidates[number - 1] : null;
    }

    /// <summary>The app went to the tray: stop decoding until it is shown again.</summary>
    public void PauseAll()
    {
        foreach (var tile in Tiles)
        {
            tile.Ptz.StopMove();
            tile.Stream.Pause();
        }
    }

    public void ResumeAll()
    {
        foreach (var tile in Tiles.Where(t => !t.IsExcluded))
        {
            tile.Stream.Resume();
        }
    }

    public Task ShutdownAsync()
    {
        foreach (var tile in Tiles)
        {
            tile.Ptz.Dispose();
        }

        return Task.WhenAll([.. Tiles.Select(t => t.Stream.ShutdownAsync()), .. Tiles.Select(t => t.Recorder.ShutdownAsync())]);
    }

    partial void OnFocusedTileChanged(CameraTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.IsHiddenByFocus = value is not null && !ReferenceEquals(tile, value);
            tile.RefreshFocus();
        }

        UpdateGrid();
    }

    private CameraTileViewModel CreateTile(CameraConfig config)
    {
        var stream = new CameraStream(config, _engine, _credentials, _dispatcher) { DefaultVolume = _defaultVolume };
        var recorder = new CameraRecorder(_engine, _credentials, () => stream.Config, () => RecordingFolder(), _dispatcher);
        if (config.ContinuousRecording)
        {
            recorder.SetContinuous(true);
        }

        return new CameraTileViewModel(stream, recorder, this);
    }

    private void OnTilesChanged()
    {
        OnPropertyChanged(nameof(HasCameras));
        OnPropertyChanged(nameof(Subtitle));
        UpdateGrid();
    }

    private void UpdateGrid()
    {
        var visible = Tiles.Count(t => t.IsVisibleInGrid);
        var size = GridLayout.Best(Math.Max(visible, 1), _stageWidth, _stageHeight);
        Rows = size.Rows;
        Columns = size.Columns;
        OnPropertyChanged(nameof(AllDetached));
    }

    private void Save() => _cameras.Save(Tiles.Select(t => t.Stream.Config));
}
