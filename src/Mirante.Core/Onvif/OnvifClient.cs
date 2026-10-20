using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mirante.Core.Security;

namespace Mirante.Core.Onvif;

public enum OnvifErrorKind
{
    AuthenticationFailed,
    Unreachable,
    Timeout,
    NotSupported,
    ProtocolError,
}

public sealed class OnvifException(OnvifErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public OnvifErrorKind Kind { get; } = kind;
}

/// <summary>
/// Minimal ONVIF (SOAP 1.2) client. The password only travels as a WS-Security
/// <c>PasswordDigest</c>; plain-text passwords are never sent, even if a camera would accept them.
/// Camera responses are untrusted: size-limited and parsed without DTDs or external entities.
/// </summary>
public sealed class OnvifClient
{
    /// <summary>Port TP-Link Tapo cameras use for ONVIF (most other brands use 80).</summary>
    public const int TapoPort = 2020;

    internal const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly CameraCredential? _credential;
    private TimeSpan _clockOffset;

    public OnvifClient(HttpClient http, Uri deviceService, CameraCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(deviceService);
        _http = http;
        DeviceService = deviceService;
        _credential = credential;
    }

    public Uri DeviceService { get; }

    public static Uri DeviceServiceFor(string host, int port = TapoPort) =>
        new UriBuilder(Uri.UriSchemeHttp, host, port, "/onvif/device_service").Uri;

    /// <summary>
    /// Reads the camera clock (no authentication needed) so WS-Security timestamps match it:
    /// cameras reject digests whose <c>Created</c> time is too far from their own clock.
    /// </summary>
    public async Task<DateTimeOffset> SyncClockAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(DeviceService, new XElement(Ns.Tds + "GetSystemDateAndTime"),
            authenticate: false, cancellationToken).ConfigureAwait(false);
        var utc = response.Descendants(Ns.Tt + "UTCDateTime").FirstOrDefault()
            ?? throw new OnvifException(OnvifErrorKind.ProtocolError, "Camera did not report its clock.");
        var date = utc.Element(Ns.Tt + "Date");
        var time = utc.Element(Ns.Tt + "Time");
        var camera = new DateTimeOffset(
            Int(date, "Year"), Int(date, "Month"), Int(date, "Day"),
            Int(time, "Hour"), Int(time, "Minute"), Int(time, "Second"), TimeSpan.Zero);
        _clockOffset = camera - DateTimeOffset.UtcNow;
        return camera;

        static int Int(XElement? parent, string name) =>
            int.Parse(parent?.Element(Ns.Tt + name)?.Value ?? "0", NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>Sends one SOAP request and returns the first element inside the response body.</summary>
    public async Task<XElement> CallAsync(Uri service, XElement request, bool authenticate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        var security = authenticate && _credential is not null
            ? BuildSecurityHeader(_credential, RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow + _clockOffset)
            : null;
        var envelope = new XElement(Ns.Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Ns.Soap.NamespaceName),
            new XElement(Ns.Soap + "Header", security),
            new XElement(Ns.Soap + "Body", request));

        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"application/soap+xml; charset=utf-8; action=\"{request.Name.NamespaceName}/{request.Name.LocalName}\"");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(service, content, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnvifException(OnvifErrorKind.Timeout, "The camera did not answer in time.", ex);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException or IOException || ex.HttpRequestError is HttpRequestError.ConnectionError)
        {
            throw new OnvifException(OnvifErrorKind.Unreachable, ex.Message, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new OnvifException(OnvifErrorKind.AuthenticationFailed, "The camera rejected the user or password.");
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var document = Parse(body);
            var soapBody = document.Root?.Element(Ns.Soap + "Body")
                ?? throw new OnvifException(OnvifErrorKind.ProtocolError, $"Not a SOAP response (HTTP {(int)response.StatusCode}).");
            var first = soapBody.Elements().FirstOrDefault()
                ?? throw new OnvifException(OnvifErrorKind.ProtocolError, "Empty SOAP body.");
            if (first.Name == Ns.Soap + "Fault")
            {
                throw ToException(first);
            }

            return first;
        }
    }

    internal static XElement BuildSecurityHeader(CameraCredential credential, byte[] nonce, DateTimeOffset created)
    {
        var createdText = created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new XElement(Ns.Wsse + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", Ns.Wsse.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wsu", Ns.Wsu.NamespaceName),
            new XElement(Ns.Wsse + "UsernameToken",
                new XElement(Ns.Wsse + "Username", credential.UserName),
                new XElement(Ns.Wsse + "Password",
                    new XAttribute("Type", Ns.PasswordDigestType),
                    PasswordDigest(nonce, createdText, credential.Password)),
                new XElement(Ns.Wsse + "Nonce",
                    new XAttribute("EncodingType", Ns.Base64EncodingType),
                    Convert.ToBase64String(nonce)),
                new XElement(Ns.Wsu + "Created", createdText)));
    }

#pragma warning disable CA5350 // WS-Security UsernameToken digest is defined with SHA-1; it only proves the password.
    internal static string PasswordDigest(byte[] nonce, string created, string password) =>
        Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created + password)]));
#pragma warning restore CA5350

    internal static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxResponseBytes,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new OnvifException(OnvifErrorKind.ProtocolError, "Invalid XML from the camera.", ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new OnvifException(OnvifErrorKind.ProtocolError, "Camera response too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes)
            {
                throw new OnvifException(OnvifErrorKind.ProtocolError, "Camera response too large.");
            }
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static OnvifException ToException(XElement fault)
    {
        var codes = fault.Descendants(Ns.Soap + "Value").Select(v => v.Value).ToList();
        var reason = fault.Descendants(Ns.Soap + "Text").FirstOrDefault()?.Value ?? "SOAP fault";
        var kind = codes.Any(c => c.EndsWith("NotAuthorized", StringComparison.Ordinal)
                                  || c.EndsWith("FailedAuthentication", StringComparison.Ordinal))
            ? OnvifErrorKind.AuthenticationFailed
            : codes.Any(c => c.EndsWith("ActionNotSupported", StringComparison.Ordinal))
                ? OnvifErrorKind.NotSupported
                : OnvifErrorKind.ProtocolError;
        return new OnvifException(kind, $"{reason} ({string.Join(" / ", codes)})");
    }
}

/// <summary>XML namespaces used by ONVIF.</summary>
public static class Ns
{
    public static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";
    public static readonly XNamespace Trt = "http://www.onvif.org/ver10/media/wsdl";
    public static readonly XNamespace Tr2 = "http://www.onvif.org/ver20/media/wsdl";
    public static readonly XNamespace Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    public static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";
    public static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    public const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

    public const string Base64EncodingType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";
}
