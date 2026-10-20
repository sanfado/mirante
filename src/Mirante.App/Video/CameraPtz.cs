using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirante.Core.Cameras;
using Mirante.Core.Onvif;
using Mirante.Core.Security;

namespace Mirante.App.Video;

/// <summary>
/// Pan/tilt of one camera, found automatically over ONVIF once its video is live; cameras without
/// it simply don't get the controls. A move is repeated while the button or key is held, and each
/// one ends by itself after <see cref="OnvifPtz.MoveTimeout"/>, so a lost stop never leaves the
/// camera turning. Used from the UI thread only.
/// </summary>
internal sealed partial class CameraPtz : ObservableObject, IDisposable
{
    /// <summary>Speed of the arrows (0–1). Half speed is easy to aim on a 360° camera.</summary>
    public const double Speed = 0.5;

    /// <summary>Where cameras usually answer ONVIF: Tapo, most brands, then common alternatives.</summary>
    private static readonly int[] Ports = [OnvifClient.TapoPort, 80, 8000, 8080, 8899];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OnvifPtz? _ptz;
    private CancellationTokenSource? _detection;
    private CancellationTokenSource? _moving;
    private (double Pan, double Tilt, double Zoom) _velocity;
    private bool _disposed;

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>Both axes at once (most ONVIF cameras; not Tapo).</summary>
    [ObservableProperty]
    private bool _supportsDiagonal;

    [ObservableProperty]
    private bool _supportsZoom;

    public bool IsMoving => _moving is not null;

    /// <summary>Looks for pan/tilt once per configuration (call again after <see cref="Reset"/>).</summary>
    public void Detect(CameraConfig config, CameraCredential? credential)
    {
        if (_disposed || _detection is not null)
        {
            return;
        }

        _detection = new CancellationTokenSource();
        _ = DetectAsync(config.Host, credential, _detection.Token);
    }

    /// <summary>The camera's address or password changed: forget what was found.</summary>
    public void Reset()
    {
        StopMove();
        _detection?.Cancel();
        _detection?.Dispose();
        _detection = null;
        _ptz = null;
        IsAvailable = false;
        SupportsDiagonal = false;
        SupportsZoom = false;
    }

#if DEBUG
    /// <summary>Demo mode only: shows the controls of a camera with every feature (no requests are sent).</summary>
    public void SimulateForDemo()
    {
        SupportsDiagonal = true;
        SupportsZoom = true;
    }
#endif

    /// <summary>Starts (or changes) a move at the given speeds, −1 to 1; positive is right / up / zoom in.</summary>
    public void StartMove(double pan, double tilt, double zoom = 0)
    {
        if (_ptz is not { } ptz || (pan == 0 && tilt == 0 && zoom == 0))
        {
            StopMove();
            return;
        }

        if (_moving is not null && _velocity == (pan, tilt, zoom))
        {
            return; // key auto-repeat, or the same arrow again
        }

        _moving?.Cancel();
        _moving?.Dispose();
        _velocity = (pan, tilt, zoom);
        _moving = new CancellationTokenSource();
        _ = MoveLoopAsync(ptz, pan, tilt, zoom, _moving.Token);
    }

    public void StopMove()
    {
        if (_moving is null)
        {
            return;
        }

        _moving.Cancel();
        _moving.Dispose();
        _moving = null;
        _velocity = (0, 0, 0);
        if (_ptz is { } ptz)
        {
            _ = SendAsync(() => ptz.StopAsync(), CancellationToken.None);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Reset();
        _disposed = true;
    }

    private async Task DetectAsync(string host, CameraCredential? credential, CancellationToken token)
    {
        foreach (var port in Ports)
        {
            try
            {
                var client = new OnvifClient(Http, OnvifClient.DeviceServiceFor(host, port), credential);
                var ptz = await OnvifPtz.ConnectAsync(client, token);
                if (!token.IsCancellationRequested && ptz is { Capabilities.Continuous: true })
                {
                    _ptz = ptz;
                    SupportsDiagonal = ptz.SupportsDiagonal;
                    SupportsZoom = ptz.Capabilities.Zoom;
                    IsAvailable = true;
                }

                return; // ONVIF answered on this port, with or without pan/tilt
            }
            catch (OnvifException ex) when (ex.Kind is OnvifErrorKind.Unreachable or OnvifErrorKind.Timeout
                                                or OnvifErrorKind.ProtocolError)
            {
                Debug.WriteLine($"PTZ: porta {port}: {ex.Kind}");
            }
            catch (OnvifException ex)
            {
                Debug.WriteLine($"PTZ: {ex.Kind}");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
            {
                Debug.WriteLine($"PTZ: porta {port}: {ex.GetType().Name}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MoveLoopAsync(OnvifPtz ptz, double pan, double tilt, double zoom, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SendAsync(() => ptz.MoveAsync(pan, tilt, zoom), token);
                await Task.Delay(RepeatEvery, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One request at a time, in order; a move whose button was already released is skipped.</summary>
    private async Task SendAsync(Func<Task> call, CancellationToken skipIfCancelled)
    {
        await _gate.WaitAsync(CancellationToken.None); // waiting keeps the order; skipping happens below
        try
        {
            if (!skipIfCancelled.IsCancellationRequested)
            {
                await call();
            }
        }
        catch (Exception ex) when (ex is OnvifException or HttpRequestException)
        {
            Debug.WriteLine($"PTZ: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
