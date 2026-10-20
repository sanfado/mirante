using System.Text.Json.Serialization;
using Mirante.Core.Cameras;

namespace Mirante.Core.Storage;

public sealed record CamerasDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public List<CameraConfig> Cameras { get; init; } = [];
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>Normal (non-fullscreen) bounds of a window, plus whether it was maximized or fullscreen.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized,
    bool IsFullscreen = false)
{
    /// <summary>Finite, positive size; a hand-edited file could hold anything.</summary>
    public bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 0 && Height > 0;
}

/// <summary>A camera that was open in its own window when the app closed.</summary>
public sealed record DetachedWindowState(Guid CameraId, WindowPlacement Placement);

/// <summary>Where a PTZ camera points in a layout (it turns there when the layout is applied).</summary>
public sealed record LayoutCameraPosition(Guid CameraId, double Pan, double Tilt, double? Zoom = null);

/// <summary>
/// A saved arrangement of the screen, e.g. "Noite": the windows (main and detached), the camera in
/// focus, the cameras left out and, optionally, where PTZ cameras point. Sound, volume and the
/// mosaic order are deliberately not part of it.
/// </summary>
public sealed record LayoutPreset
{
    public const int MaxNameLength = 40;
    public const int MaxCount = 20;

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public WindowPlacement? MainWindow { get; init; }
    public List<DetachedWindowState> DetachedWindows { get; init; } = [];
    public Guid? FocusedCamera { get; init; }

    /// <summary>
    /// Cameras left out (paused while the layout is on). The excluded ones are stored, not the
    /// included ones, so a camera added later shows up in every layout instead of being hidden.
    /// </summary>
    public List<Guid> ExcludedCameras { get; init; } = [];

    /// <summary>Null: applying the layout doesn't move any camera.</summary>
    public List<LayoutCameraPosition>? CameraPositions { get; init; }

    /// <summary>A clean copy of a layout read from disk, or null if it can't be used.</summary>
    internal LayoutPreset? Normalized()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MaxNameLength)
        {
            return null;
        }

        return this with
        {
            Name = Name.Trim(),
            MainWindow = MainWindow is { IsValid: true } ? MainWindow : null,
            DetachedWindows = [.. (DetachedWindows ?? [])
                .Where(d => d is { Placement.IsValid: true })
                .DistinctBy(d => d.CameraId)],
            ExcludedCameras = [.. (ExcludedCameras ?? []).Distinct()],
            CameraPositions = CameraPositions is null ? null : [.. CameraPositions
                .Where(p => p is not null && new Cameras.CameraPosition("x", p.Pan, p.Tilt, p.Zoom).IsValid)
                .DistinctBy(p => p.CameraId)],
        };
    }
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Null means the default folder (see <c>AppPaths</c>).</summary>
    public string? SnapshotFolder { get; init; }

    public WindowPlacement? MainWindow { get; init; }

    public List<DetachedWindowState> DetachedWindows { get; init; } = [];

    /// <summary>Closing the main window hides it in the notification area instead of exiting.</summary>
    public bool KeepInTray { get; init; }

    /// <summary>
    /// Opt-out (not opt-in) on purpose: settings files written before this existed load
    /// missing booleans as false, and update checks should stay on for them.
    /// </summary>
    public bool DisableUpdateChecks { get; init; }

    public const int DefaultVolume = 80;

    /// <summary>Audio volume 0–100. Nullable so files written before it existed get the default, not 0.</summary>
    public int? Volume { get; init; }

    public List<LayoutPreset> Layouts { get; init; } = [];

    /// <summary>Where recordings go, in order of preference. Empty: the default folder (see <c>AppPaths</c>).</summary>
    public List<Recording.RecordingLocation> RecordingLocations { get; init; } = [];

    /// <summary>Continuous recordings older than this are deleted. Null: the default (7).</summary>
    public int? RecordingRetentionDays { get; init; }

    /// <summary>Cameras left out by the layout in use (paused), so the app reopens as it was left.</summary>
    public List<Guid> HiddenCameras { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CamerasDocument))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Recording.RecordingLocation))]
internal sealed partial class MiranteJsonContext : JsonSerializerContext;
