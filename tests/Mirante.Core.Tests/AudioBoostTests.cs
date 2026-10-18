using Mirante.Core.Cameras;
using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class AudioBoostTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(80, 80, 0)]
    [InlineData(100, 100, 0)]
    [InlineData(150, 100, 6)]
    [InlineData(200, 100, 12)]
    [InlineData(999, 100, 12)] // tampered config: clamped
    [InlineData(-5, 0, 0)]
    public void Volume_above_100_becomes_software_gain(int volume, int player, double preamp)
    {
        var (playerVolume, preampDb) = AudioBoost.Split(volume);
        Assert.Equal(player, playerVolume);
        Assert.Equal(preamp, preampDb, precision: 6);
    }

    [Fact]
    public void Voice_boost_adds_a_little_gain_within_limits()
    {
        Assert.Equal(AudioBoost.VoicePreampDb, AudioBoost.Split(100, voiceBoost: true).PreampDb, precision: 6);
        Assert.Equal(AudioBoost.MaxBoostDb + AudioBoost.VoicePreampDb, AudioBoost.Split(200, voiceBoost: true).PreampDb, precision: 6);
        Assert.True(AudioBoost.Split(200, voiceBoost: true).PreampDb <= AudioBoost.MaxPreampDb);
    }

    [Fact]
    public void Equalizer_only_when_needed()
    {
        Assert.False(AudioBoost.NeedsEqualizer(100, voiceBoost: false));
        Assert.True(AudioBoost.NeedsEqualizer(101, voiceBoost: false));
        Assert.True(AudioBoost.NeedsEqualizer(50, voiceBoost: true));
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
