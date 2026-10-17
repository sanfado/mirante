using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Mirante.Core.Cameras;

public enum CameraValidationError
{
    NameRequired,
    NameTooLong,
    InvalidHost,
    InvalidPort,
    InvalidStreamPath,
}

/// <summary>
/// Validates user input and anything loaded from disk before it reaches the network
/// or the video engine (a tampered config file must not inject arbitrary URLs).
/// </summary>
public static partial class CameraValidator
{
    public const int MaxNameLength = 40;
    private const int MaxHostLength = 253;
    private const int MaxPathLength = 200;

    public static IReadOnlyList<CameraValidationError> Validate(CameraConfig camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var errors = new List<CameraValidationError>();

        if (string.IsNullOrWhiteSpace(camera.Name))
        {
            errors.Add(CameraValidationError.NameRequired);
        }
        else if (camera.Name.Trim().Length > MaxNameLength)
        {
            errors.Add(CameraValidationError.NameTooLong);
        }

        if (!IsValidHost(camera.Host))
        {
            errors.Add(CameraValidationError.InvalidHost);
        }

        if (camera.Port is < 1 or > 65535)
        {
            errors.Add(CameraValidationError.InvalidPort);
        }

        if (!IsValidStreamPath(camera.StreamPath))
        {
            errors.Add(CameraValidationError.InvalidStreamPath);
        }

        return errors;
    }

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength || host.Trim() != host)
        {
            return false;
        }

        // "192.168.1" parses as an IPv4 address, but it is almost always a typo.
        if (IPv4Like().IsMatch(host))
        {
            return host.Count(c => c == '.') == 3 && IPAddress.TryParse(host, out _);
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return ip.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    public static bool IsValidStreamPath(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= MaxPathLength
        && !path.StartsWith('/')
        && !path.Contains("..", StringComparison.Ordinal)
        && StreamPathPattern().IsMatch(path);

    [GeneratedRegex(@"^[0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Like();

    [GeneratedRegex(@"^[A-Za-z0-9_\-./]+(\?[A-Za-z0-9_\-.=&]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamPathPattern();
}
