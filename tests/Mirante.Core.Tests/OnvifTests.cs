using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mirante.Core.Onvif;
using Mirante.Core.Security;

namespace Mirante.Core.Tests;

public sealed class OnvifTests
{
    private static readonly Uri Device = OnvifClient.DeviceServiceFor("192.168.1.100");

    [Fact]
    public void Password_digest_matches_the_onvif_guide_example() =>
        Assert.Equal("tuOSpGlFlIXsozq4HFNeeGeFLEI=", OnvifClient.PasswordDigest(
            Convert.FromBase64String("LKqI6G/AikKCQrN0zqZFlg=="), "2010-09-16T07:50:45Z", "userpassword"));

    [Fact]
    public void Password_is_never_sent_in_plain_text()
    {
        var header = OnvifClient.BuildSecurityHeader(new CameraCredential("camera-user", "segredo"),
            new byte[16], DateTimeOffset.UtcNow).ToString();
        Assert.DoesNotContain("segredo", header, StringComparison.Ordinal);
        Assert.Contains("#PasswordDigest", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connects_to_the_profile_with_ptz_and_reads_capabilities()
    {
        var camera = new FakeOnvifCamera("camera-user", "certa");
        var ptz = await Connect(camera, "certa");

        Assert.NotNull(ptz);
        Assert.Equal("profile_ptz", ptz.ProfileToken);
        Assert.Equal(new PtzCapabilities(Continuous: true, Relative: true, Absolute: true, MaxPresets: 8), ptz.Capabilities);
    }

    [Fact]
    public async Task Requests_stay_on_the_configured_host_even_if_the_camera_names_another()
    {
        var camera = new FakeOnvifCamera("camera-user", "certa");
        var ptz = await Connect(camera, "certa");
        await ptz!.StopAsync(TestContext.Current.CancellationToken);

        Assert.All(camera.Requests, r => Assert.Equal(Device.Authority, r.Uri.Authority));
        Assert.Contains(camera.Requests, r => r.Uri.AbsolutePath == "/onvif/ptz_service");
    }

    [Fact]
    public async Task Timestamps_follow_the_camera_clock()
    {
        // The camera is an hour ahead and rejects digests created more than 5 minutes off its clock.
        var camera = new FakeOnvifCamera("camera-user", "certa") { Clock = DateTimeOffset.UtcNow.AddHours(1) };
        Assert.NotNull(await Connect(camera, "certa"));
    }

    [Fact]
    public async Task Wrong_password_is_an_authentication_failure()
    {
        var camera = new FakeOnvifCamera("camera-user", "certa");
        var ex = await Assert.ThrowsAsync<OnvifException>(() => Connect(camera, "errada"));
        Assert.Equal(OnvifErrorKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task Camera_without_ptz_returns_null()
    {
        var camera = new FakeOnvifCamera("camera-user", "certa") { HasPtz = false };
        Assert.Null(await Connect(camera, "certa"));
    }

    [Fact]
    public async Task Move_uses_invariant_numbers_clamped_and_a_safety_timeout()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("pt-BR"); // decimal comma
        try
        {
            var camera = new FakeOnvifCamera("camera-user", "certa");
            var ptz = await Connect(camera, "certa");
            await ptz!.MoveAsync(1.5, -0.25, TestContext.Current.CancellationToken);

            var move = camera.Requests[^1].Body;
            Assert.Equal("ContinuousMove", move.Name.LocalName);
            var panTilt = move.Descendants(Ns.Tt + "PanTilt").Single();
            Assert.Equal("1", panTilt.Attribute("x")?.Value);
            Assert.Equal("-0.25", panTilt.Attribute("y")?.Value);
            Assert.Equal("PT2S", move.Element(Ns.Tptz + "Timeout")?.Value);
            Assert.Equal("profile_ptz", move.Element(Ns.Tptz + "ProfileToken")?.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Reads_the_current_position()
    {
        var camera = new FakeOnvifCamera("camera-user", "certa");
        var ptz = await Connect(camera, "certa");
        Assert.Equal(new PtzPosition(0.141223, -0.5), await ptz!.GetPositionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Xml_with_a_dtd_is_rejected()
    {
        const string evil = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><x>&a;</x>""";
        Assert.Equal(OnvifErrorKind.ProtocolError, Assert.Throws<OnvifException>(() => OnvifClient.Parse(evil)).Kind);
    }

    private static async Task<OnvifPtz?> Connect(FakeOnvifCamera camera, string password)
    {
        var http = new HttpClient(camera);
        var client = new OnvifClient(http, Device, new CameraCredential("camera-user", password));
        return await OnvifPtz.ConnectAsync(client, TestContext.Current.CancellationToken);
    }

    /// <summary>ONVIF camera double that checks WS-Security digests independently of the code under test.</summary>
    private sealed class FakeOnvifCamera(string user, string password) : HttpMessageHandler
    {
        public DateTimeOffset Clock { get; init; } = DateTimeOffset.UtcNow;

        public bool HasPtz { get; init; } = true;

        public List<(Uri Uri, XElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var envelope = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).Root!;
            var body = envelope.Element(Ns.Soap + "Body")!.Elements().Single();
            Requests.Add((request.RequestUri!, body));

            if (body.Name.LocalName != "GetSystemDateAndTime" && !Authorized(envelope))
            {
                return Soap(HttpStatusCode.BadRequest, """
                    <s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code>
                    <s:Reason><s:Text xml:lang="en">Sender not authorized</s:Text></s:Reason></s:Fault>
                    """);
            }

            var ptzService = HasPtz
                ? """<tds:Service><tds:Namespace>http://www.onvif.org/ver20/ptz/wsdl</tds:Namespace><tds:XAddr>http://203.0.113.9:2020/onvif/ptz_service</tds:XAddr></tds:Service>"""
                : string.Empty;
            return Soap(HttpStatusCode.OK, body.Name.LocalName switch
            {
                "GetSystemDateAndTime" => $"""
                    <tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:UTCDateTime>
                    <tt:Time><tt:Hour>{Clock.Hour}</tt:Hour><tt:Minute>{Clock.Minute}</tt:Minute><tt:Second>{Clock.Second}</tt:Second></tt:Time>
                    <tt:Date><tt:Year>{Clock.Year}</tt:Year><tt:Month>{Clock.Month}</tt:Month><tt:Day>{Clock.Day}</tt:Day></tt:Date>
                    </tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>
                    """,
                "GetServices" => $"""
                    <tds:GetServicesResponse>
                    <tds:Service><tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace><tds:XAddr>http://203.0.113.9:2020/onvif/service</tds:XAddr></tds:Service>
                    {ptzService}
                    </tds:GetServicesResponse>
                    """,
                "GetProfiles" => """
                    <trt:GetProfilesResponse>
                    <trt:Profiles token="profile_jpeg"><tt:Name>jpeg</tt:Name></trt:Profiles>
                    <trt:Profiles token="profile_ptz"><tt:Name>main</tt:Name><tt:PTZConfiguration token="PTZTOKEN" /></trt:Profiles>
                    </trt:GetProfilesResponse>
                    """,
                "GetNodes" => """
                    <tptz:GetNodesResponse><tptz:PTZNode token="n"><tt:SupportedPTZSpaces>
                    <tt:AbsolutePanTiltPositionSpace /><tt:RelativePanTiltTranslationSpace /><tt:ContinuousPanTiltVelocitySpace />
                    </tt:SupportedPTZSpaces><tt:MaximumNumberOfPresets>8</tt:MaximumNumberOfPresets></tptz:PTZNode></tptz:GetNodesResponse>
                    """,
                "GetStatus" => """
                    <tptz:GetStatusResponse><tptz:PTZStatus><tt:Position><tt:PanTilt x="0.141223" y="-0.5" /></tt:Position></tptz:PTZStatus></tptz:GetStatusResponse>
                    """,
                var other => $"<tptz:{other}Response />",
            });
        }

        private bool Authorized(XElement envelope)
        {
            var token = envelope.Descendants(Ns.Wsse + "UsernameToken").FirstOrDefault();
            if (token is null || token.Element(Ns.Wsse + "Username")?.Value != user)
            {
                return false;
            }

            var created = token.Element(Ns.Wsu + "Created")!.Value;
            var nonce = Convert.FromBase64String(token.Element(Ns.Wsse + "Nonce")!.Value);
            var createdAt = DateTimeOffset.Parse(created, CultureInfo.InvariantCulture);
            if ((createdAt - Clock).Duration() > TimeSpan.FromMinutes(5))
            {
                return false;
            }

#pragma warning disable CA5350 // test double for WS-Security UsernameToken (SHA-1 by spec)
            var expected = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created + password)]));
#pragma warning restore CA5350
            return token.Element(Ns.Wsse + "Password")?.Value == expected;
        }

        private static HttpResponseMessage Soap(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent($"""
                <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                  xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                  xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:ter="http://www.onvif.org/ver10/error">
                <s:Body>{body}</s:Body></s:Envelope>
                """, Encoding.UTF8, "application/soap+xml"),
        };
    }
}
