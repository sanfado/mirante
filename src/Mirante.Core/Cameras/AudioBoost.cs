namespace Mirante.Core.Cameras;

/// <summary>
/// How Mirante turns "volume up to 200%" and "Realçar voz" into LibVLC settings.
/// Security cameras record quiet, uneven sound, so speech often needs more than 100%.
/// Both are per player, so they change live, per camera.
/// </summary>
public static class AudioBoost
{
    public const int MaxVolume = 200;
    public const int DefaultVolume = 100;

    /// <summary>
    /// LibVLC 3's equalizer scales its output by 0.25 (<c>EQZ_IN_FACTOR</c>, about −12 dB); VLC's own
    /// "Flat" preset uses a +12 dB preamp to make up for it. Without this, turning the equalizer on
    /// makes everything quieter. 20·log10(4) dB.
    /// </summary>
    public const double EqualizerUnityPreampDb = 12.04;

    /// <summary>"Realçar voz" removes low-frequency energy; this gives some loudness back.</summary>
    public const double VoicePreampDb = 3;

    /// <summary>Equalizer preamp for "Realçar voz" (within LibVLC's ±20 dB limit).</summary>
    public const double VoiceEqualizerPreampDb = EqualizerUnityPreampDb + VoicePreampDb;

    /// <summary>
    /// Speech curve for LibVLC 3's 10-band equalizer (ISO bands: 31, 62, 125, 250, 500 Hz,
    /// 1, 2, 4, 8, 16 kHz): cuts hum/rumble below ~200 Hz and hiss at the top, lifts 1–4 kHz
    /// where intelligibility is.
    /// </summary>
    public static IReadOnlyList<double> VoiceBands { get; } = [-12, -10, -6, -1, 1, 3, 5, 4, -1, -6];

    /// <summary>Extra gain at 200%. Higher values mostly add distortion and noise.</summary>
    public const double MaxBoostDb = 12;

    public static int Clamp(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>
    /// The LibVLC player volume for a 0–200% slider. Above 100 LibVLC's audio output amplifies in
    /// software with gain = (volume/100)³; the slider maps linearly in dB instead, from 0 dB at 100%
    /// to <see cref="MaxBoostDb"/> at 200% (≈ +1.2 dB per 10%).
    /// </summary>
    public static int PlayerVolume(int volume)
    {
        var v = Clamp(volume);
        if (v <= 100)
        {
            return v;
        }

        var boostDb = (v - 100) / 100.0 * MaxBoostDb;
        return (int)Math.Round(100 * Math.Pow(10, boostDb / 60));
    }
}
