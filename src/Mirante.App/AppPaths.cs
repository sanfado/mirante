namespace Mirante.App;

/// <summary>Where the app keeps its files. Nothing is written next to the executable.</summary>
internal static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mirante");

    public static string LogFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirante", "logs");

    public static string CamerasFile => Path.Combine(DataFolder, "cameras.json");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>
    /// %USERPROFILE%\Pictures\Mirante — on purpose not the "Pictures" known folder, which
    /// OneDrive often redirects: snapshots of a home camera stay on this PC.
    /// </summary>
    public static string DefaultSnapshotFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Mirante");
}
