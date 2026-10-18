using Mirante.Core.Cameras;
using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class AudioBoostTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(80, 80)]
    [InlineData(100, 100)]
    [InlineData(150, 126)] // +6 dB
    [InlineData(200, 158)] // +12 dB: LibVLC's gain is (volume/100)³
    [InlineData(999, 158)] // tampered config: clamped
    [InlineData(-5, 0)]
    public void Volume_above_100_maps_linearly_in_dB(int volume, int player) =>
        Assert.Equal(player, AudioBoost.PlayerVolume(volume));

    [Fact]
    public void Max_volume_is_12_dB_above_unity() =>
        Assert.Equal(AudioBoost.MaxBoostDb, 60 * Math.Log10(AudioBoost.PlayerVolume(AudioBoost.MaxVolume) / 100.0), precision: 0);

    [Fact]
    public void Voice_boost_compensates_the_equalizer_attenuation()
    {
        // LibVLC 3's equalizer outputs 0.25 × the signal; the preamp must give those 12 dB back
        // (a flat equalizer at 0 dB made everything quieter in 0.3.0).
        Assert.Equal(AudioBoost.EqualizerUnityPreampDb, 20 * Math.Log10(4), precision: 2);
        Assert.Equal(AudioBoost.EqualizerUnityPreampDb + AudioBoost.VoicePreampDb, AudioBoost.VoiceEqualizerPreampDb, precision: 6);
        Assert.True(AudioBoost.VoiceEqualizerPreampDb <= 20); // LibVLC's preamp limit
    }

    [Fact]
    public void Voice_curve_matches_vlc_band_count() => Assert.Equal(10, AudioBoost.VoiceBands.Count);

    [Fact]
    public void Audio_settings_are_saved_per_camera()
    {
        var repo = new CameraRepository(Path.Combine(_folder, "cameras.json"));
        var camera = CameraConfig.CreateNew("Sala", "192.168.1.100") with { Volume = 170, VoiceBoost = true };
        repo.Save([camera]);
        var loaded = Assert.Single(repo.Load());
        Assert.Equal(170, loaded.Volume);
        Assert.True(loaded.VoiceBoost);
    }

    [Fact]
    public void Cameras_saved_before_audio_settings_load_with_defaults()
    {
        var path = Path.Combine(_folder, "cameras.json");
        File.WriteAllText(path, """
            { "version": 1, "cameras": [
              { "id": "6f7d3f0e-58a1-4a8b-9a51-0d2f5a1b2c3d", "name": "Sala", "host": "192.168.1.100", "port": 554, "streamPath": "stream1" }
            ] }
            """);
        var loaded = Assert.Single(new CameraRepository(path).Load());
        Assert.Null(loaded.Volume);
        Assert.False(loaded.VoiceBoost);
    }
}
