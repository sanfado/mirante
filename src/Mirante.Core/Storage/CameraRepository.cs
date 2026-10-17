using Mirante.Core.Cameras;

namespace Mirante.Core.Storage;

/// <summary>Persists the camera list (without credentials) as JSON.</summary>
public sealed class CameraRepository(string filePath)
{
    private readonly JsonFileStore<CamerasDocument> _store = new(filePath, MiranteJsonContext.Default.CamerasDocument);

    /// <summary>Loads cameras, silently dropping entries that fail validation (e.g. a hand-edited file).</summary>
    public IReadOnlyList<CameraConfig> Load() =>
        (_store.Load()?.Cameras ?? []) // "cameras": null or missing in a hand-edited file
            .Where(c => c is not null && CameraValidator.Validate(c).Count == 0)
            .DistinctBy(c => c.Id)
            .ToList();

    public void Save(IEnumerable<CameraConfig> cameras) =>
        _store.Save(new CamerasDocument { Cameras = [.. cameras] });
}

public sealed class SettingsRepository(string filePath)
{
    private readonly JsonFileStore<AppSettings> _store = new(filePath, MiranteJsonContext.Default.AppSettings);

    /// <summary>
    /// Loads settings. Lists missing from older files deserialize as null (the JSON source
    /// generator skips property initializers), so they are normalized here.
    /// </summary>
    public AppSettings Load() => _store.Load() is { } settings
        ? settings with { DetachedWindows = settings.DetachedWindows ?? [] }
        : new AppSettings();

    public void Save(AppSettings settings) => _store.Save(settings);
}
