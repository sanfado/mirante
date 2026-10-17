using Mirante.Core.Cameras;
using Mirante.Core.Layout;
using Mirante.Core.Security;

namespace Mirante.Core.Tests;

public class CameraValidatorTests
{
    private static CameraConfig Valid() => CameraConfig.CreateNew("Sala", "192.168.1.100");

    [Fact]
    public void Valid_camera_has_no_errors() => Assert.Empty(CameraValidator.Validate(Valid()));

    [Theory]
    [InlineData("192.168.1.100", true)]
    [InlineData("camera-sala.local", true)]
    [InlineData("fe80::1", true)]
    [InlineData("192.168.1", false)]
    [InlineData("999.1.1.1", false)]
    [InlineData(" 192.168.1.100", false)]
    [InlineData("", false)]
    [InlineData("evil.com/path", false)]
    [InlineData("user:pass@host", false)]
    public void Host_validation(string host, bool expected) => Assert.Equal(expected, CameraValidator.IsValidHost(host));

    [Theory]
    [InlineData("stream1", true)]
    [InlineData("Streaming/Channels/101", true)]
    [InlineData("live?channel=1&subtype=0", true)]
    [InlineData("/stream1", false)]
    [InlineData("../etc/passwd", false)]
    [InlineData("stream1 extra", false)]
    [InlineData("", false)]
    public void Stream_path_validation(string path, bool expected) =>
        Assert.Equal(expected, CameraValidator.IsValidStreamPath(path));

    [Fact]
    public void Reports_every_problem()
    {
        var camera = Valid() with { Name = " ", Host = "x y", Port = 0, StreamPath = ".." };
        Assert.Equal(
            [CameraValidationError.NameRequired, CameraValidationError.InvalidHost,
             CameraValidationError.InvalidPort, CameraValidationError.InvalidStreamPath],
            CameraValidator.Validate(camera));
    }

    [Fact]
    public void Name_too_long() =>
        Assert.Contains(CameraValidationError.NameTooLong,
            CameraValidator.Validate(Valid() with { Name = new string('a', CameraValidator.MaxNameLength + 1) }));
}

public class RtspAddressTests
{
    [Fact]
    public void Builds_url_without_credentials() =>
        Assert.Equal("rtsp://192.168.1.100:554/stream1",
            RtspAddress.Build(CameraConfig.CreateNew("Sala", "192.168.1.100")).AbsoluteUri);

    [Fact]
    public void Wraps_ipv6_in_brackets() =>
        Assert.Equal("rtsp://[fe80::1]:8554/stream2",
            RtspAddress.Build(CameraConfig.CreateNew("X", "fe80::1", 8554, "stream2")).AbsoluteUri);
}

public class CameraCredentialTests
{
    [Fact]
    public void ToString_never_reveals_the_password()
    {
        var text = new CameraCredential("camera-user", "segredo123").ToString();
        Assert.DoesNotContain("segredo123", text, StringComparison.Ordinal);
        Assert.Contains("camera-user", text, StringComparison.Ordinal);
    }
}

public class GridLayoutTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 2, 3)]
    [InlineData(9, 3, 3)]
    public void Landscape_area(int count, int rows, int columns) =>
        Assert.Equal(new GridSize(rows, columns), GridLayout.Best(count, 2560, 1400));

    [Fact]
    public void Portrait_monitor_stacks_cameras() =>
        Assert.Equal(new GridSize(2, 1), GridLayout.Best(2, 1440, 2500));

    [Fact]
    public void Zero_cameras() => Assert.Equal(new GridSize(0, 0), GridLayout.Best(0, 100, 100));

    [Fact]
    public void Unknown_size_falls_back_to_square() => Assert.Equal(new GridSize(2, 2), GridLayout.Best(3, 0, 0));
}

public class SnapshotNamingTests
{
    [Fact]
    public void Sanitizes_and_avoids_collisions()
    {
        var folder = Directory.CreateTempSubdirectory("mirante-test-").FullName;
        try
        {
            var when = new DateTime(2026, 10, 17, 19, 42, 3);
            var first = SnapshotNaming.NextPath(folder, "Sala/Cozinha", when);
            Assert.Equal(Path.Combine(folder, "Sala_Cozinha_2026-10-17_19-42-03.png"), first);
            File.WriteAllText(first, "x");
            Assert.EndsWith("_2.png", SnapshotNaming.NextPath(folder, "Sala/Cozinha", when), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
