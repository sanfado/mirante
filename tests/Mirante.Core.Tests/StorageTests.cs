using Mirante.Core.Cameras;
using Mirante.Core.Platform.Windows;
using Mirante.Core.Security;
using Mirante.Core.Storage;

namespace Mirante.Core.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;

    private string FilePath => Path.Combine(_folder, "cameras.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Round_trip()
    {
        var repo = new CameraRepository(FilePath);
        var cameras = new[] { CameraConfig.CreateNew("Sala", "192.168.1.100"), CameraConfig.CreateNew("Garagem", "192.168.1.101", streamPath: "stream2") };
        repo.Save(cameras);
        Assert.Equal(cameras, repo.Load());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Missing_file_means_no_cameras() => Assert.Empty(new CameraRepository(FilePath).Load());

    [Fact]
    public void Corrupt_file_is_kept_aside_not_overwritten()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new CameraRepository(FilePath).Load());
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_folder, "cameras.json.corrupt-*"));
    }

    [Fact]
    public void Invalid_entries_from_a_tampered_file_are_dropped()
    {
        File.WriteAllText(FilePath, """
            { "version": 1, "cameras": [
              { "id": "6f7d3f0e-58a1-4a8b-9a51-0d2f5a1b2c3d", "name": "Ok", "host": "192.168.1.100", "port": 554, "streamPath": "stream1" },
              { "id": "0a7d3f0e-58a1-4a8b-9a51-0d2f5a1b2c3d", "name": "Bad", "host": "evil.com/x", "port": 554, "streamPath": "../../x" }
            ] }
            """);
        Assert.Equal("Ok", Assert.Single(new CameraRepository(FilePath).Load()).Name);
    }

    [Fact]
    public void File_without_camera_list_loads_as_empty()
    {
        File.WriteAllText(FilePath, """{ "version": 1 }""");
        Assert.Empty(new CameraRepository(FilePath).Load());
    }

    [Fact]
    public void Config_file_never_contains_credentials()
    {
        new CameraRepository(FilePath).Save([CameraConfig.CreateNew("Sala", "192.168.1.100")]);
        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("senha", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_default_when_missing() =>
        Assert.Equal(AppTheme.Dark, new SettingsRepository(Path.Combine(_folder, "settings.json")).Load().Theme);
}

public class WindowsCredentialStoreTests
{
    // Separate prefix so tests never touch the real app's entries.
    private readonly WindowsCredentialStore _store = new($"Mirante.Tests/{Guid.NewGuid():N}/");

    [Fact]
    public void Write_read_delete()
    {
        var id = Guid.NewGuid();
        try
        {
            _store.Write(id, new CameraCredential("camera-user", "çenha:/@#ü 123"));
            var read = _store.Read(id);
            Assert.NotNull(read);
            Assert.Equal("camera-user", read.UserName);
            Assert.Equal("çenha:/@#ü 123", read.Password);

            _store.Write(id, new CameraCredential("camera-user", "nova"));
            Assert.Equal("nova", _store.Read(id)!.Password);
        }
        finally
        {
            _store.Delete(id);
        }

        Assert.Null(_store.Read(id));
    }

    [Fact]
    public void DeleteAll_removes_only_entries_under_its_prefix()
    {
        var other = new WindowsCredentialStore($"Mirante.Tests/{Guid.NewGuid():N}/");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        try
        {
            _store.Write(a, new CameraCredential("u", "1"));
            _store.Write(b, new CameraCredential("u", "2"));
            other.Write(c, new CameraCredential("u", "3"));

            Assert.Equal(2, _store.DeleteAll());
            Assert.Null(_store.Read(a));
            Assert.Null(_store.Read(b));
            Assert.NotNull(other.Read(c));
            Assert.Equal(0, _store.DeleteAll());
        }
        finally
        {
            other.Delete(c);
        }
    }

    [Fact]
    public void Missing_entry_reads_as_null_and_delete_is_idempotent()
    {
        var id = Guid.NewGuid();
        Assert.Null(_store.Read(id));
        _store.Delete(id);
    }
}
