namespace Mirante.Core.Cameras;

/// <summary>
/// A saved pan/tilt (and zoom) position, e.g. "Porta". Kept in Mirante's camera list, not in the
/// camera, so nothing is written to the device and there is no camera-side limit. Values use
/// ONVIF's generic space: −1 to 1 (zoom 0 to 1).
/// </summary>
public sealed record CameraPosition(string Name, double Pan, double Tilt, double? Zoom = null)
{
    public const int MaxNameLength = 40;

    /// <summary>Per camera; a menu longer than this stops being useful.</summary>
    public const int MaxCount = 20;

    /// <summary>Checked on load: a hand-edited file must not send the camera out-of-range values.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && Name.Trim().Length <= MaxNameLength
        && InRange(Pan) && InRange(Tilt)
        && (Zoom is not { } zoom || InRange(zoom));

    private static bool InRange(double value) => double.IsFinite(value) && value is >= -1 and <= 1;
}
