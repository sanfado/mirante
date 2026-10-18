namespace Mirante.Core.Cameras;

/// <summary>
/// How Mirante turns "volume up to 200%" and "Realçar voz" into audio processing.
/// Security cameras record quiet, uneven sound, so speech often needs more than 100%.
/// Everything runs through LibVLC's per-player equalizer, so it changes live, per camera.
/// </summary>
public static class AudioBoost
{
    public const int MaxVolume = 200;
    public const int DefaultVolume = 100;

    /// <summary>Extra gain at 200%. Higher values mostly add distortion and noise.</summary>
    public const double MaxBoostDb = 12;

    /// <summary>"Realçar voz" removes low-frequency energy; this gives some loudness back.</summary>
    public const double VoicePreampDb = 3;

    /// <summary>LibVLC's equalizer pre-amplification limit.</summary>
    public const double MaxPreampDb = 20;

    /// <summary>
    /// Speech curve for LibVLC 3's 10-band equalizer (ISO bands: 31, 62, 125, 250, 500 Hz,
    /// 1, 2, 4, 8, 16 kHz): cuts hum/rumble below ~200 Hz and hiss at the top, lifts 1–4 kHz
    /// where intelligibility is.
    /// </summary>
    public static IReadOnlyList<double> VoiceBands { get; } = [-12, -10, -6, -1, 1, 3, 5, 4, -1, -6];

    public static int Clamp(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>
    /// Splits a 0–200% volume into the player volume (0–100, never above unity on the output
    /// device) and a software pre-amplification in dB (0 up to <see cref="MaxBoostDb"/> above 100%),
    /// plus the small voice compensation when "Realçar voz" is on.
    /// </summary>
    public static (int PlayerVolume, double PreampDb) Split(int volume, bool voiceBoost = false)
    {
        var v = Clamp(volume);
        var (player, boost) = v <= 100 ? (v, 0.0) : (100, (v - 100) / 100.0 * MaxBoostDb);
        return (player, Math.Min(boost + (voiceBoost ? VoicePreampDb : 0), MaxPreampDb));
    }

    /// <summary>The equalizer is only needed when amplifying or enhancing voice.</summary>
    public static bool NeedsEqualizer(int volume, bool voiceBoost) => voiceBoost || Clamp(volume) > 100;
}
