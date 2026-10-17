using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Window_layout_round_trips()
    {
        var repo = new SettingsRepository(Path.Combine(_folder, "settings.json"));
        var camera = Guid.NewGuid();
        repo.Save(new AppSettings
        {
            KeepInTray = true,
            MainWindow = new WindowPlacement(-2560, 0, 1280, 800, IsMaximized: false, IsFullscreen: true),
            DetachedWindows = [new DetachedWindowState(camera, new WindowPlacement(2600, -1000, 960, 590, false))],
        });

        var loaded = repo.Load();
        Assert.True(loaded.KeepInTray);
        Assert.Equal(new WindowPlacement(-2560, 0, 1280, 800, false, true), loaded.MainWindow);
        var detached = Assert.Single(loaded.DetachedWindows);
        Assert.Equal(camera, detached.CameraId);
        Assert.False(detached.Placement.IsFullscreen);
    }

    [Fact]
    public void Settings_written_by_version_0_1_still_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, """
            { "version": 1, "theme": "Light", "snapshotFolder": null,
              "mainWindow": { "left": 10, "top": 20, "width": 1280, "height": 800, "isMaximized": true } }
            """);
        var loaded = new SettingsRepository(path).Load();
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.False(loaded.MainWindow!.IsFullscreen);
        Assert.Empty(loaded.DetachedWindows);
        Assert.False(loaded.KeepInTray);
    }
}
