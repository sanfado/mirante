using System.Net;
using System.Net.Sockets;

namespace Mirante.Core.Cameras;

public static class RtspAddress
{
    /// <summary>
    /// Builds the stream URL <b>without</b> credentials, so it is safe to show and log.
    /// Credentials are handed to the video engine separately.
    /// </summary>
    public static Uri Build(CameraConfig camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var host = IPAddress.TryParse(camera.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{camera.Host}]"
            : camera.Host;
        return new Uri($"rtsp://{host}:{camera.Port}/{camera.StreamPath}", UriKind.Absolute);
    }
}
