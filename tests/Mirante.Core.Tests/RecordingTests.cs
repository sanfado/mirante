using Mirante.Core.Cameras;
using Mirante.Core.Recording;
using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class RecordingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Paths_are_per_camera_and_day_and_never_overwrite()
    {
        var start = new DateTime(2026, 10, 20, 14, 0, 0);
        var first = RecordingFiles.NextPath(_folder, "Sala", start, RecordingKind.Continuous);
        Assert.Equal(Path.Combine(_folder, "Sala", "2026-10-20", "14-00-00 contínua.mkv"), first);

        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllBytes(first, [1]);
        var second = RecordingFiles.NextPath(_folder, "Sala", start, RecordingKind.Continuous);
        Assert.Equal(Path.Combine(_folder, "Sala", "2026-10-20", "14-00-00 contínua (2).mkv"), second);

        Assert.EndsWith("14-00-00 manual.mkv", RecordingFiles.NextPath(_folder, "Sala", start, RecordingKind.Manual), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sala", "Sala")]
    [InlineData("Garagem: fundos/portão", "Garagem_ fundos_portão")]
    [InlineData("O'Neil {teste}", "O_Neil _teste_")]
    [InlineData("  ..  ", "camera")]
    public void Camera_names_become_safe_folder_names(string name, string expected) =>
        Assert.Equal(expected, RecordingFiles.SafeName(name));

    [Theory]
    [InlineData(@"Sala\2026-10-20\14-00-00 contínua.mkv", RecordingKind.Continuous)]
    [InlineData(@"Sala\2026-10-20\14-00-00 contínua (3).mkv", RecordingKind.Continuous)]
    [InlineData(@"Sala\2026-10-20\09-15-42 manual.mkv", RecordingKind.Manual)]
    [InlineData(@"Sala\2026-10-20\aniversário.mkv", null)]
    [InlineData(@"Sala\2026-10-20\14-00-00 contínua.mp4", null)]
    [InlineData(@"Sala\fotos\14-00-00 contínua.mkv", null)]
    public void Only_files_named_by_mirante_count_as_recordings(string relative, RecordingKind? kind) =>
        Assert.Equal(kind, RecordingFiles.KindOf(Path.Combine(_folder, relative)));

    [Fact]
    public void Scan_ignores_other_files_in_the_folder()
    {
        var day = Directory.CreateDirectory(Path.Combine(_folder, "Sala", "2026-10-20")).FullName;
        File.WriteAllBytes(Path.Combine(day, "14-00-00 contínua.mkv"), new byte[10]);
        File.WriteAllBytes(Path.Combine(day, "15-00-00 manual.mkv"), new byte[20]);
        File.WriteAllBytes(Path.Combine(day, "férias.mkv"), new byte[30]);
        File.WriteAllBytes(Path.Combine(_folder, "14-00-00 contínua.mkv"), new byte[40]);

        var found = RecordingFiles.Scan(_folder);
        Assert.Equal(2, found.Count);
        Assert.Equal(30, found.Sum(f => f.Size));
    }

    [Fact]
    public void Cleanup_removes_old_continuous_recordings_but_never_manual_ones()
    {
        var files = new[]
        {
            Rec("old-continuous", 100, days: 8, RecordingKind.Continuous),
            Rec("old-manual", 100, days: 30, RecordingKind.Manual),
            Rec("recent-continuous", 100, days: 1, RecordingKind.Continuous),
        };
        var chosen = RecordingFiles.SelectForCleanup(files, Now, retentionDays: 7, maxBytes: long.MaxValue, Empty());
        Assert.Equal(["old-continuous"], chosen.Select(f => f.Path));
    }

    [Fact]
    public void Cleanup_frees_space_oldest_first_and_skips_files_being_written()
    {
        var files = new[]
        {
            Rec("c1", 400, days: 3, RecordingKind.Continuous),
            Rec("c2", 400, days: 2, RecordingKind.Continuous),
            Rec("c3", 400, days: 1, RecordingKind.Continuous),
            Rec("manual", 400, days: 5, RecordingKind.Manual),
            Rec("writing", 400, days: 0, RecordingKind.Continuous),
        };
        var chosen = RecordingFiles.SelectForCleanup(files, Now, retentionDays: 7, maxBytes: 1000,
            new HashSet<string>(["writing"], StringComparer.OrdinalIgnoreCase));
        // 2000 in total, limit 1000: the oldest continuous ones go until it fits (c1, c2, c3 -> 800);
        // the manual recording and the file being written are never candidates.
        Assert.Equal(["c1", "c2", "c3"], chosen.Select(f => f.Path));
    }

    [Theory]
    [InlineData(@"D:\Gravações", true)]
    [InlineData(@"C:\Users\Ana\Videos\Mirante", true)]
    [InlineData(@"C:\Users\O'Neil\Videos", false)]
    [InlineData(@"D:\cams}{x", false)]
    [InlineData(@"relativo\pasta", false)]
    [InlineData("", false)]
    public void Folders_with_characters_that_could_break_the_video_engine_are_refused(string folder, bool usable) =>
        Assert.Equal(usable, RecordingFiles.IsUsableFolder(folder));

    [Fact]
    public void Limits_are_clamped_and_default_when_missing()
    {
        Assert.Equal(7, RecordingFiles.ClampRetentionDays(null));
        Assert.Equal(100, RecordingFiles.ClampMaxGigabytes(null));
        Assert.Equal(1, RecordingFiles.ClampRetentionDays(-5));
        Assert.Equal(RecordingFiles.MaxGigabytes, RecordingFiles.ClampMaxGigabytes(int.MaxValue));
    }

    [Fact]
    public void Continuous_recording_is_saved_per_camera_and_off_for_old_files()
    {
        var repo = new CameraRepository(Path.Combine(_folder, "cameras.json"));
        repo.Save([CameraConfig.CreateNew("Sala", "192.168.1.100") with { ContinuousRecording = true }]);
        Assert.True(Assert.Single(repo.Load()).ContinuousRecording);

        var old = Path.Combine(_folder, "old.json");
        File.WriteAllText(old, """
            { "version": 1, "cameras": [
              { "id": "6f7d3f0e-58a1-4a8b-9a51-0d2f5a1b2c3d", "name": "Sala", "host": "192.168.1.100", "port": 554, "streamPath": "stream1" }
            ] }
            """);
        Assert.False(Assert.Single(new CameraRepository(old).Load()).ContinuousRecording);
    }

    private static RecordingFile Rec(string name, long size, int days, RecordingKind kind) =>
        new(name, size, Now.AddDays(-days).AddMinutes(-1), kind);

    private static HashSet<string> Empty() => new(StringComparer.OrdinalIgnoreCase);
}
