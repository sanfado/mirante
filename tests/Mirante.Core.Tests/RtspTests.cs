using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mirante.Core.Rtsp;
using Mirante.Core.Security;

namespace Mirante.Core.Tests;

public class RtspAuthenticationTests
{
    [Fact]
    public void Digest_matches_rfc2617_example()
    {
        var challenge = RtspAuthentication.ParseParameters(
            "realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");
        var header = RtspAuthentication.BuildDigest(challenge, new CameraCredential("Mufasa", "Circle Of Life"),
            "GET", "/dir/index.html", clientNonce: "0a4f113b");
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header, StringComparison.Ordinal);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefers_digest_over_basic()
    {
        var header = RtspAuthentication.BuildAuthorization(
            ["Basic realm=\"IP Camera\"", "Digest realm=\"IP Camera\", nonce=\"abc\""],
            new CameraCredential("u", "p"), "DESCRIBE", "rtsp://x/stream1");
        Assert.StartsWith("Digest ", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_digest_algorithm() =>
        Assert.Null(RtspAuthentication.BuildDigest(
            RtspAuthentication.ParseParameters("realm=\"r\", nonce=\"n\", algorithm=SHA-512-256"),
            new CameraCredential("u", "p"), "DESCRIBE", "rtsp://x/"));
}

public sealed class RtspProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correct_password_is_ok(bool useQop)
    {
        await using var server = FakeRtspCamera.Start("camera-user", "certa", useQop);
        var result = await RtspProbe.ProbeAsync(server.Uri, new CameraCredential("camera-user", "certa"), Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(RtspProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_authentication_failure()
    {
        await using var server = FakeRtspCamera.Start("camera-user", "certa");
        var result = await RtspProbe.ProbeAsync(server.Uri, new CameraCredential("camera-user", "errada"), Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(RtspProbeStatus.AuthenticationFailed, result.Status);
    }

    [Fact]
    public async Task Missing_credentials_is_authentication_failure()
    {
        await using var server = FakeRtspCamera.Start("camera-user", "certa");
        var result = await RtspProbe.ProbeAsync(server.Uri, null, Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(RtspProbeStatus.AuthenticationFailed, result.Status);
    }

    [Fact]
    public async Task Closed_port_is_unreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await RtspProbe.ProbeAsync(new Uri($"rtsp://127.0.0.1:{port}/stream1"), null, Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(RtspProbeStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var uri = new Uri($"rtsp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/stream1");
            var result = await RtspProbe.ProbeAsync(uri, null, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            Assert.Equal(RtspProbeStatus.Timeout, result.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Tiny RTSP server that checks Digest auth independently of the code under test.</summary>
    private sealed class FakeRtspCamera : IAsyncDisposable
    {
        private const string Realm = "IP Camera";
        private const string Nonce = "0123456789abcdef0123456789abcdef";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly string _user;
        private readonly string _password;
        private readonly bool _useQop;
        private Task? _loop;

        private FakeRtspCamera(string user, string password, bool useQop)
        {
            _user = user;
            _password = password;
            _useQop = useQop;
        }

        public Uri Uri => new($"rtsp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream1");

        public static FakeRtspCamera Start(string user, string password, bool useQop = false)
        {
            var server = new FakeRtspCamera(user, password, useQop);
            server._listener.Start();
            server._loop = server.AcceptLoopAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop!;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, _cts.Token);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var auth = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.Ordinal));
                var ok = auth is not null && IsValid(auth["Authorization:".Length..].Trim(), request.Split(' ')[1]);
                var qop = _useQop ? ", qop=\"auth\"" : string.Empty;
                var response = ok
                    ? "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n"
                    : $"RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Basic realm=\"{Realm}\"\r\nWWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\"{qop}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
            }
        }

        private bool IsValid(string header, string uri)
        {
            if (!header.StartsWith("Digest ", StringComparison.Ordinal))
            {
                return false;
            }

            var p = RtspAuthentication.ParseParameters(header[7..]);
            var ha1 = Md5($"{p["username"]}:{Realm}:{_password}");
            var ha2 = Md5($"DESCRIBE:{uri}");
            var expected = _useQop
                ? Md5($"{ha1}:{Nonce}:{p["nc"]}:{p["cnonce"]}:auth:{ha2}")
                : Md5($"{ha1}:{Nonce}:{ha2}");
            return p["username"] == _user && p["uri"] == uri && p["response"] == expected;
        }

#pragma warning disable CA5351 // test double for RFC 2617 Digest
        private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
#pragma warning restore CA5351
    }
}
