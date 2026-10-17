using System.Globalization;

namespace Mirante.Core.Cameras;

public static class SnapshotNaming
{
    /// <summary>Returns a unique, filesystem-safe path like <c>Sala_2026-10-17_19-42-03.png</c>.</summary>
    public static string NextPath(string folder, string cameraName, DateTime timestamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string([.. (cameraName ?? string.Empty).Trim().Select(c => invalid.Contains(c) ? '_' : c)]);
        if (safeName.Length == 0)
        {
            safeName = "camera";
        }

        var stem = $"{safeName}_{timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(folder, stem + ".png");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{stem}_{i}.png");
        }

        return path;
    }
}
