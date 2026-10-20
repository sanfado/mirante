using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class LayoutSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Layouts_round_trip()
    {
        var sala = Guid.NewGuid();
        var garagem = Guid.NewGuid();
        var layout = new LayoutPreset
        {
            Id = Guid.NewGuid(),
            Name = "Noite",
            MainWindow = new WindowPlacement(0, 0, 1280, 720, IsMaximized: false, IsFullscreen: true),
            DetachedWindows = [new DetachedWindowState(garagem, new WindowPlacement(-1920, 0, 800, 600, false))],
            FocusedCamera = sala,
            ExcludedCameras = [Guid.NewGuid()],
            CameraPositions = [new LayoutCameraPosition(sala, 0.14, -0.28)],
        };
        var repo = new SettingsRepository(FilePath);
        repo.Save(new AppSettings { Layouts = [layout], HiddenCameras = [garagem] });

        var loaded = repo.Load();
        var back = Assert.Single(loaded.Layouts);
        Assert.Equal(layout.Name, back.Name);
        Assert.Equal(layout.MainWindow, back.MainWindow);
        Assert.Equal(layout.DetachedWindows, back.DetachedWindows);
        Assert.Equal(layout.FocusedCamera, back.FocusedCamera);
        Assert.Equal(layout.ExcludedCameras, back.ExcludedCameras);
        Assert.Equal(layout.CameraPositions, back.CameraPositions);
        Assert.Equal([garagem], loaded.HiddenCameras);
    }

    [Fact]
    public void Settings_from_before_layouts_load_with_none()
    {
        File.WriteAllText(FilePath, """{ "version": 1, "theme": "Dark" }""");
        var settings = new SettingsRepository(FilePath).Load();
        Assert.Empty(settings.Layouts);
        Assert.Empty(settings.HiddenCameras);
    }

    [Fact]
    public void Layout_without_camera_moves_keeps_null()
    {
        var repo = new SettingsRepository(FilePath);
        repo.Save(new AppSettings { Layouts = [new LayoutPreset { Id = Guid.NewGuid(), Name = "Dia" }] });
        Assert.Null(Assert.Single(repo.Load().Layouts).CameraPositions);
    }

    [Fact]
    public void Tampered_layouts_are_cleaned_on_load()
    {
        var many = string.Join(",", Enumerable.Range(1, 30).Select(i => $$"""{ "id": "{{Guid.NewGuid()}}", "name": "L{{i}}" }"""));
        File.WriteAllText(FilePath, $$"""
            { "version": 1,
              "mainWindow": { "left": 0, "top": 0, "width": -5, "height": 600, "isMaximized": false },
              "layouts": [
                null,
                { "id": "{{Guid.NewGuid()}}", "name": "" },
                { "id": "{{Guid.NewGuid()}}", "name": "{{new string('x', 41)}}" },
                { "id": "00000000-0000-0000-0000-000000000000", "name": "Sem id" },
                { "id": "{{Guid.NewGuid()}}", "name": "  Noite  ",
                  "mainWindow": { "left": 0, "top": 0, "width": 0, "height": 600, "isMaximized": false },
                  "detachedWindows": [ null, { "cameraId": "{{Guid.NewGuid()}}", "placement": { "left": 0, "top": 0, "width": 800, "height": 600, "isMaximized": false } } ],
                  "cameraPositions": [ { "cameraId": "{{Guid.NewGuid()}}", "pan": 7, "tilt": 0 }, { "cameraId": "{{Guid.NewGuid()}}", "pan": 0.5, "tilt": 0.1 } ] },
                {{many}}
              ] }
            """);
        var settings = new SettingsRepository(FilePath).Load();
        Assert.Null(settings.MainWindow);
        Assert.Equal(LayoutPreset.MaxCount, settings.Layouts.Count);
        var noite = settings.Layouts[0];
        Assert.Equal("Noite", noite.Name);
        Assert.Null(noite.MainWindow);
        Assert.Single(noite.DetachedWindows);
        Assert.Equal(0.5, Assert.Single(noite.CameraPositions!).Pan);
    }
}

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
        Assert.False(loaded.DisableUpdateChecks); // update checks stay on for existing users
        Assert.Null(loaded.Volume); // the app applies AppSettings.DefaultVolume, not 0 (silence)
    }
}
