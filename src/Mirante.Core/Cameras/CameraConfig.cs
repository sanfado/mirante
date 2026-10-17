namespace Mirante.Core.Cameras;

/// <summary>
/// Connection settings for one camera. Deliberately holds no credentials:
/// those live in an <see cref="Security.ICredentialStore"/>.
/// </summary>
public sealed record CameraConfig
{
    public const int DefaultRtspPort = 554;
    public const string HighQualityStream = "stream1";
    public const string LowQualityStream = "stream2";

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = DefaultRtspPort;
    public string StreamPath { get; init; } = HighQualityStream;

    public static CameraConfig CreateNew(string name, string host, int port = DefaultRtspPort,
        string streamPath = HighQualityStream) =>
        new() { Id = Guid.NewGuid(), Name = name, Host = host, Port = port, StreamPath = streamPath };
}
