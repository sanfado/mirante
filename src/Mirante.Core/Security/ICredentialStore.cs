namespace Mirante.Core.Security;

/// <summary>Secure storage for camera credentials, keyed by camera id.</summary>
public interface ICredentialStore
{
    CameraCredential? Read(Guid cameraId);

    void Write(Guid cameraId, CameraCredential credential);

    void Delete(Guid cameraId);
}
