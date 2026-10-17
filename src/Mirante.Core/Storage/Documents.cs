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

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Null means the default folder (see <c>AppPaths</c>).</summary>
    public string? SnapshotFolder { get; init; }

    public WindowPlacement? MainWindow { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CamerasDocument))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class MiranteJsonContext : JsonSerializerContext;
