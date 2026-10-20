using Mirante.Core.Cameras;
using Mirante.Core.Recording;
using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class RecordingStorageTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly RecordingLocation Disk1 = new() { Id = Guid.NewGuid(), Folder = @"D:\Mirante", MaxGigabytes = 100 };
    private static readonly RecordingLocation Disk2 = new() { Id = Guid.NewGuid(), Folder = @"E:\Mirante", MaxGigabytes = 100 };
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Automatic_uses_the_first_location_with_room()
    {
        Assert.Equal(Disk1, RecordingStorage.Choose([State(Disk1, used: 50), State(Disk2, used: 0)], null));
        Assert.Equal(Disk2, RecordingStorage.Choose([State(Disk1, used: 100), State(Disk2, used: 10)], null));
    }

    [Fact]
    public void When_every_location_is_full_the_one_with_the_oldest_footage_is_reused()
    {
        var states = new[]
        {
            State(Disk1, used: 100, oldestDaysAgo: 2),
            State(Disk2, used: 100, oldestDaysAgo: 5),
        };
        Assert.Equal(Disk2, RecordingStorage.Choose(states, null));
    }

    [Fact]
    public void A_fixed_location_is_used_even_at_its_limit_but_not_when_its_disk_cannot_take_it()
    {
        Assert.Equal(Disk2, RecordingStorage.Choose([State(Disk1, used: 0), State(Disk2, used: 100)], Disk2.Id));
        // Unplugged or almost full disk: the camera keeps recording elsewhere instead of losing footage.
        Assert.Equal(Disk1, RecordingStorage.Choose([State(Disk1, used: 0), State(Disk2, used: 0, available: false)], Disk2.Id));
        Assert.Equal(Disk1, RecordingStorage.Choose([State(Disk1, used: 0), State(Disk2, used: 0, freeGb: 4)], Disk2.Id));
    }

    [Fact]
    public void Nowhere_to_record_when_no_disk_can_take_it() =>
        Assert.Null(RecordingStorage.Choose([State(Disk1, used: 0, freeGb: 1), State(Disk2, used: 0, available: false)], null));

    [Fact]
    public void Locations_are_cleaned_up_and_never_empty()
    {
        var list = RecordingStorage.Normalize(
        [
            Disk1,
            Disk1 with { Id = Guid.NewGuid(), Folder = @"d:\mirante\" }, // same folder
            new RecordingLocation { Id = Guid.NewGuid(), Folder = "relativo" },
            new RecordingLocation { Id = Guid.NewGuid(), Folder = @"C:\Users\O'Neil" },
            null,
            Disk2,
        ], @"C:\Videos\Mirante");
        Assert.Equal([Disk1.Id, Disk2.Id], list.Select(l => l.Id));

        var fallback = Assert.Single(RecordingStorage.Normalize([], @"C:\Videos\Mirante"));
        Assert.Equal(@"C:\Videos\Mirante", fallback.Folder);
        Assert.Equal(RecordingStorage.Default("x").Id, fallback.Id);
    }

    [Fact]
    public void Files_belong_to_the_location_whose_folder_holds_them()
    {
        var root = new RecordingLocation { Id = Guid.NewGuid(), Folder = @"F:\" };
        RecordingLocation[] all = [Disk1, Disk2, root];
        Assert.Equal(Disk2, RecordingStorage.LocationOf(all, @"E:\Mirante\Sala\2026-10-20\14-00-00 contínua.mkv"));
        Assert.Equal(root, RecordingStorage.LocationOf(all, @"F:\Sala\2026-10-20\14-00-00 contínua.mkv"));
        Assert.Null(RecordingStorage.LocationOf(all, @"D:\Mirante2\Sala\x.mkv"));
    }

    [Fact]
    public void Locations_and_camera_choice_are_saved()
    {
        var settings = new SettingsRepository(Path.Combine(_folder, "settings.json"));
        settings.Save(new AppSettings { RecordingLocations = [Disk1, Disk2 with { MaxGigabytes = 500 }] });
        var loaded = settings.Load().RecordingLocations;
        Assert.Equal([Disk1.Id, Disk2.Id], loaded.Select(l => l.Id));
        Assert.Equal(500, loaded[1].Gigabytes);

        var cameras = new CameraRepository(Path.Combine(_folder, "cameras.json"));
        cameras.Save([CameraConfig.CreateNew("Sala", "192.168.1.100") with { RecordingLocation = Disk2.Id }]);
        Assert.Equal(Disk2.Id, Assert.Single(cameras.Load()).RecordingLocation);
    }

    private static LocationState State(RecordingLocation location, long used, int? oldestDaysAgo = null,
        bool available = true, long freeGb = 500) =>
        new(location, available, freeGb * Gb, used * Gb,
            oldestDaysAgo is { } days ? DateTime.UtcNow.AddDays(-days) : null);
}
