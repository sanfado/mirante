using LibVLCSharp.Shared;
using Mirante.Core.Security;

namespace Mirante.App.Video;

/// <summary>Single LibVLC instance shared by all cameras.</summary>
internal sealed class VideoEngine : IDisposable
{
    public VideoEngine()
    {
        LibVLCSharp.Shared.Core.Initialize();
        string[] options =
        [
            "--ignore-config",        // don't read a vlcrc the user may have from VLC itself
            "--keystore=memory",      // never persist credentials to disk
            "--no-video-title-show",
            "--no-osd",
            "--no-spu",
            "--no-snapshot-preview",
            // DirectSound gives each camera its own mute/volume. The default (WASAPI via mmdevice)
            // shares one audio session per process, so muting one camera muted all of them.
            "--aout=directsound,any",
        ];
#if DEBUG
        // Debug only: MIRANTE_VLC_LOG=<file> writes LibVLC's own log (never in release builds:
        // it can include URLs and device details).
        if (Environment.GetEnvironmentVariable("MIRANTE_VLC_LOG") is { Length: > 0 } logFile)
        {
            LibVlc = new LibVLC(enableDebugLogs: true, [.. options, "--verbose=2"]);
            var gate = new object();
            LibVlc.Log += (_, e) =>
            {
                lock (gate)
                {
                    File.AppendAllText(logFile, $"[{e.Level}] {e.Module}: {e.Message}{Environment.NewLine}");
                }
            };
            return;
        }
#endif
        LibVlc = new LibVLC(enableDebugLogs: false, [.. options, "--quiet"]);
    }

    public LibVLC LibVlc { get; }

    /// <summary>
    /// Creates the media with credentials passed as options, never inside the URL,
    /// so they can't leak through URL logging.
    /// </summary>
    public Media CreateMedia(Uri uri, CameraCredential credential) =>
        new(LibVlc, uri,
            ":rtsp-tcp",
            ":network-caching=400",
            $":rtsp-user={credential.UserName}",
            $":rtsp-pwd={credential.Password}");

#if DEBUG
    /// <summary>Debug-only: replaces real cameras with local clips (see Debug/DemoMode).</summary>
    public Func<Mirante.Core.Cameras.CameraConfig, Media>? DemoMedia { get; set; }
#endif

    public Media CreateMedia(Mirante.Core.Cameras.CameraConfig camera, CameraCredential credential)
    {
#if DEBUG
        if (DemoMedia is not null)
        {
            return DemoMedia(camera);
        }
#endif
        return CreateMedia(Mirante.Core.Cameras.RtspAddress.Build(camera), credential);
    }

    /// <summary>
    /// The camera's stream copied into an MKV file, as it arrives (no decoding, no re-encoding).
    /// MKV keeps H.264/H.265 and the cameras' G.711 audio, and stays playable if the app or the PC
    /// stops abruptly. Nothing is shown or played.
    /// </summary>
    public Media CreateRecordingMedia(Mirante.Core.Cameras.CameraConfig camera, CameraCredential credential, string path)
    {
        // The path goes inside LibVLC's output settings: it must not be able to change them.
        if (path.IndexOfAny(['\'', '"', '{', '}', '\0']) >= 0 || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Recording path not allowed.", nameof(path));
        }

        var media = CreateMedia(camera, credential);
        media.AddOption(":network-caching=1000");
        media.AddOption($":sout=#std{{access=file,mux=mkv,dst='{path}'}}");
        return media;
    }

    public void Dispose() => LibVlc.Dispose();
}
