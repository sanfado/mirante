using System.Globalization;
using System.Xml.Linq;

namespace Mirante.Core.Onvif;

/// <summary>Pan/tilt position in ONVIF's generic space (both axes −1 to 1).</summary>
public sealed record PtzPosition(double Pan, double Tilt);

public sealed record PtzCapabilities(bool Continuous, bool Relative, bool Absolute, int MaxPresets, bool Zoom = false);

/// <summary>
/// Pan/tilt control of one camera over ONVIF (e.g. Tapo C200/C210). Only what Mirante needs:
/// move while a button is held, stop, read the position and go back to one.
/// </summary>
public sealed class OnvifPtz
{
    /// <summary>
    /// A continuous move ends by itself after this, so a lost "stop" (app closed, Wi-Fi drop)
    /// never leaves the camera spinning. Callers repeat the move while the button is held.
    /// </summary>
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(2);

    private readonly OnvifClient _client;
    private readonly Uri _service;

    private OnvifPtz(OnvifClient client, Uri service, string profileToken, PtzCapabilities capabilities,
        string manufacturer, string model)
    {
        _client = client;
        _service = service;
        ProfileToken = profileToken;
        Capabilities = capabilities;
        Manufacturer = manufacturer;
        Model = model;
    }

    public string ProfileToken { get; }

    public PtzCapabilities Capabilities { get; }

    public string Manufacturer { get; }

    public string Model { get; }

    /// <summary>
    /// ONVIF moves both axes at once when asked, and most cameras do. TP-Link Tapo cameras run a
    /// diagonal as one axis until its end and then the other, so for them it's one direction at a time.
    /// </summary>
    public bool SupportsDiagonal => !IsTapo(Manufacturer, Model);

    internal static bool IsTapo(string manufacturer, string model) =>
        manufacturer.Contains("tp-link", StringComparison.OrdinalIgnoreCase)
        || manufacturer.Contains("tplink", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("Tapo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the PTZ service and a media profile with PTZ; <c>null</c> if the camera has none.</summary>
    public static async Task<OnvifPtz?> ConnectAsync(OnvifClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.SyncClockAsync(cancellationToken).ConfigureAwait(false);

        var services = await client.CallAsync(client.DeviceService,
            new XElement(Ns.Tds + "GetServices", new XElement(Ns.Tds + "IncludeCapability", false)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var ptzService = ServiceAddress(client.DeviceService, services, Ns.Tptz);
        if (ptzService is null)
        {
            return null;
        }

        var mediaService = ServiceAddress(client.DeviceService, services, Ns.Trt) ?? client.DeviceService;
        var profiles = await client.CallAsync(mediaService, new XElement(Ns.Trt + "GetProfiles"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var token = profiles.Elements(Ns.Trt + "Profiles")
            .FirstOrDefault(p => p.Element(Ns.Tt + "PTZConfiguration") is not null)
            ?.Attribute("token")?.Value;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var nodes = await client.CallAsync(ptzService, new XElement(Ns.Tptz + "GetNodes"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var spaces = nodes.Descendants(Ns.Tt + "SupportedPTZSpaces").FirstOrDefault();
        var maxPresets = int.TryParse(nodes.Descendants(Ns.Tt + "MaximumNumberOfPresets").FirstOrDefault()?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var max) ? max : 0;
        var capabilities = new PtzCapabilities(
            Continuous: spaces?.Element(Ns.Tt + "ContinuousPanTiltVelocitySpace") is not null,
            Relative: spaces?.Element(Ns.Tt + "RelativePanTiltTranslationSpace") is not null,
            Absolute: spaces?.Element(Ns.Tt + "AbsolutePanTiltPositionSpace") is not null,
            MaxPresets: max,
            Zoom: spaces?.Element(Ns.Tt + "ContinuousZoomVelocitySpace") is not null);

        // Manufacturer and model only adjust behavior (see SupportsDiagonal); optional for cameras.
        string manufacturer = string.Empty, model = string.Empty;
        try
        {
            var info = await client.CallAsync(client.DeviceService, new XElement(Ns.Tds + "GetDeviceInformation"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            manufacturer = info.Element(Ns.Tds + "Manufacturer")?.Value.Trim() ?? string.Empty;
            model = info.Element(Ns.Tds + "Model")?.Value.Trim() ?? string.Empty;
        }
        catch (OnvifException ex) when (ex.Kind is OnvifErrorKind.NotSupported or OnvifErrorKind.ProtocolError)
        {
        }

        return new OnvifPtz(client, ptzService, token, capabilities, manufacturer, model);
    }

    /// <summary>
    /// Starts moving at the given speeds (−1 to 1; positive = right / up / zoom in). An axis left at
    /// zero is left out of the request, so a zoom-only move doesn't touch pan/tilt and vice versa.
    /// </summary>
    public Task MoveAsync(double pan, double tilt, double zoom = 0, CancellationToken cancellationToken = default)
    {
        var velocity = new XElement(Ns.Tptz + "Velocity");
        if (pan != 0 || tilt != 0 || zoom == 0)
        {
            velocity.Add(PanTilt(pan, tilt));
        }

        if (zoom != 0)
        {
            velocity.Add(new XElement(Ns.Tt + "Zoom",
                new XAttribute(XNamespace.Xmlns + "tt", Ns.Tt.NamespaceName),
                new XAttribute("x", Number(zoom))));
        }

        return CallAsync(new XElement(Ns.Tptz + "ContinuousMove",
            Profile(),
            velocity,
            new XElement(Ns.Tptz + "Timeout", XmlDuration(MoveTimeout))), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        CallAsync(new XElement(Ns.Tptz + "Stop",
            Profile(),
            new XElement(Ns.Tptz + "PanTilt", true),
            new XElement(Ns.Tptz + "Zoom", Capabilities.Zoom)), cancellationToken);

    /// <summary>Moves by a fraction of the full range (−1 to 1 on each axis).</summary>
    public Task StepAsync(double pan, double tilt, CancellationToken cancellationToken = default) =>
        CallAsync(new XElement(Ns.Tptz + "RelativeMove",
            Profile(),
            new XElement(Ns.Tptz + "Translation", PanTilt(pan, tilt))), cancellationToken);

    public Task MoveToAsync(PtzPosition position, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        return CallAsync(new XElement(Ns.Tptz + "AbsoluteMove",
            Profile(),
            new XElement(Ns.Tptz + "Position", PanTilt(position.Pan, position.Tilt))), cancellationToken);
    }

    public async Task<PtzPosition?> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        var status = await CallAsync(new XElement(Ns.Tptz + "GetStatus", Profile()), cancellationToken)
            .ConfigureAwait(false);
        var panTilt = status.Descendants(Ns.Tt + "Position").Elements(Ns.Tt + "PanTilt").FirstOrDefault();
        return panTilt is not null
               && TryNumber(panTilt.Attribute("x")?.Value, out var x)
               && TryNumber(panTilt.Attribute("y")?.Value, out var y)
            ? new PtzPosition(x, y)
            : null;
    }

    /// <summary>
    /// Service address from GetServices. Only its path is used: requests (and the password
    /// digest) always go to the host and port the user configured, never to one the camera names.
    /// </summary>
    internal static Uri? ServiceAddress(Uri deviceService, XElement services, XNamespace service)
    {
        var address = services.Elements(Ns.Tds + "Service")
            .Where(s => s.Element(Ns.Tds + "Namespace")?.Value == service.NamespaceName)
            .Select(s => s.Element(Ns.Tds + "XAddr")?.Value)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (address is null || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new UriBuilder(deviceService) { Path = uri.AbsolutePath, Query = string.Empty }.Uri;
    }

    internal static string Number(double value) =>
        Math.Clamp(value, -1, 1).ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string XmlDuration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"PT{duration.TotalSeconds:0.###}S");

    private static XElement PanTilt(double pan, double tilt) =>
        new(Ns.Tt + "PanTilt",
            new XAttribute(XNamespace.Xmlns + "tt", Ns.Tt.NamespaceName),
            new XAttribute("x", Number(pan)),
            new XAttribute("y", Number(tilt)));

    private XElement Profile() => new(Ns.Tptz + "ProfileToken", ProfileToken);

    private Task<XElement> CallAsync(XElement request, CancellationToken cancellationToken) =>
        _client.CallAsync(_service, request, cancellationToken: cancellationToken);
}
