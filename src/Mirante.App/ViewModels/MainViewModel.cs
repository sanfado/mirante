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

    [ObservableProperty]
    private int _volume = AppSettings.DefaultVolume;

    public MainViewModel(CameraRepository cameras, ICredentialStore credentials, VideoEngine engine,
        Func<string> snapshotFolder, Dispatcher dispatcher)
    {
        _cameras = cameras;
        _credentials = credentials;
        _engine = engine;
        _dispatcher = dispatcher;
        SnapshotFolder = snapshotFolder;
        foreach (var config in cameras.Load())
        {
            Tiles.Add(CreateTile(config));
        }

        Tiles.CollectionChanged += (_, _) => OnTilesChanged();
        OnTilesChanged();
    }

    public IShell? Shell { get; set; }

    public Func<string> SnapshotFolder { get; set; }

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = [];

    public ObservableCollection<CameraTileViewModel> DetachedTiles { get; } = [];

    public bool HasCameras => Tiles.Count > 0;

    public bool HasAudioTile => AudioTile is not null;

    /// <summary>Turns this camera's sound on (muting any other) or off.</summary>
    public void ToggleAudio(CameraTileViewModel tile) =>
        AudioTile = ReferenceEquals(AudioTile, tile) ? null : tile;

    partial void OnAudioTileChanged(CameraTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.Stream.SetAudio(ReferenceEquals(tile, value), Volume);
            tile.RefreshAudio();
        }
    }

    partial void OnVolumeChanged(int value) => AudioTile?.Stream.SetAudio(true, value);

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

    public bool AllDetached => Tiles.Count > 0 && Tiles.All(t => t.IsDetached);

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
        _credentials.Delete(tile.Id);
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
        var candidates = Tiles.Where(t => !t.IsDetached).ToList();
        FocusedTile = number >= 1 && number <= candidates.Count ? candidates[number - 1] : null;
    }

    /// <summary>The app went to the tray: stop decoding until it is shown again.</summary>
    public void PauseAll()
    {
        foreach (var tile in Tiles)
        {
            tile.Stream.Pause();
        }
    }

    public void ResumeAll()
    {
        foreach (var tile in Tiles)
        {
            tile.Stream.Resume();
        }
    }

    public Task ShutdownAsync() => Task.WhenAll(Tiles.Select(t => t.Stream.ShutdownAsync()));

    partial void OnFocusedTileChanged(CameraTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.IsHiddenByFocus = value is not null && !ReferenceEquals(tile, value);
            tile.RefreshFocus();
        }

        UpdateGrid();
    }

    private CameraTileViewModel CreateTile(CameraConfig config) =>
        new(new CameraStream(config, _engine, _credentials, _dispatcher), this);

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
