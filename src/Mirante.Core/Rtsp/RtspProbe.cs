using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Mirante.Core.Security;

namespace Mirante.Core.Rtsp;

public enum RtspProbeStatus
{
    Ok,
    AuthenticationFailed,
    Unreachable,
    Timeout,
    ProtocolError,
}

public sealed record RtspProbeResult(RtspProbeStatus Status, string? Detail = null);

/// <summary>
/// Minimal RTSP client that only sends DESCRIBE. Used to tell the user precisely what is
/// wrong ("wrong password" vs "camera unreachable"), which the video engine cannot do.
/// </summary>
public static class RtspProbe
{
    private const int MaxResponseBytes = 64 * 1024;

    public static async Task<RtspProbeResult> ProbeAsync(Uri uri, CameraCredential? credential,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var first = await DescribeAsync(uri, 1, authorization: null, cts.Token).ConfigureAwait(false);
            if (first.StatusCode == 200)
            {
                return new RtspProbeResult(RtspProbeStatus.Ok);
            }

            if (first.StatusCode != 401)
            {
                return new RtspProbeResult(RtspProbeStatus.ProtocolError, $"RTSP {first.StatusCode}");
            }

            if (credential is null)
            {
                return new RtspProbeResult(RtspProbeStatus.AuthenticationFailed);
            }

            var authorization = RtspAuthentication.BuildAuthorization(
                first.Challenges, credential, "DESCRIBE", uri.AbsoluteUri);
            if (authorization is null)
            {
                return new RtspProbeResult(RtspProbeStatus.ProtocolError, "Unsupported authentication scheme");
            }

            // Fresh connection: some cameras close the socket after a 401.
            var second = await DescribeAsync(uri, 2, authorization, cts.Token).ConfigureAwait(false);
            return second.StatusCode switch
            {
                200 => new RtspProbeResult(RtspProbeStatus.Ok),
                401 or 403 => new RtspProbeResult(RtspProbeStatus.AuthenticationFailed),
                _ => new RtspProbeResult(RtspProbeStatus.ProtocolError, $"RTSP {second.StatusCode}"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RtspProbeResult(RtspProbeStatus.Timeout);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new RtspProbeResult(RtspProbeStatus.Unreachable, ex.Message);
        }
        catch (FormatException ex)
        {
            return new RtspProbeResult(RtspProbeStatus.ProtocolError, ex.Message);
        }
    }

    private static async Task<RtspResponse> DescribeAsync(Uri uri, int cseq, string? authorization,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(uri.DnsSafeHost, uri.Port, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();

        var request = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"DESCRIBE {uri.AbsoluteUri} RTSP/1.0\r\n")
            .Append(CultureInfo.InvariantCulture, $"CSeq: {cseq}\r\n")
            .Append("Accept: application/sdp\r\n")
            .Append("User-Agent: Mirante\r\n");
        if (authorization is not null)
        {
            request.Append(CultureInfo.InvariantCulture, $"Authorization: {authorization}\r\n");
        }

        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);
        return await ReadResponseHeadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RtspResponse> ReadResponseHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Connection closed by the camera.");
            }

            received.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return RtspResponse.Parse(text[..end]);
            }

            if (received.Length > MaxResponseBytes)
            {
                throw new FormatException("RTSP response header too large.");
            }
        }
    }

    internal sealed record RtspResponse(int StatusCode, IReadOnlyList<string> Challenges)
    {
        public static RtspResponse Parse(string head)
        {
            var lines = head.Split("\r\n");
            var status = lines[0].Split(' ', 3);
            if (status.Length < 2 || !status[0].StartsWith("RTSP/", StringComparison.Ordinal)
                || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                throw new FormatException("Not an RTSP response.");
            }

            var challenges = lines.Skip(1)
                .Where(l => l.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase))
                .Select(l => l["WWW-Authenticate:".Length..].Trim())
                .ToList();
            return new RtspResponse(code, challenges);
        }
    }
}
