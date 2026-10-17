namespace Mirante.Core.Security;

/// <summary>
/// User name and password of a camera account. Not a record on purpose: the
/// compiler-generated ToString of a record would print the password into logs.
/// </summary>
public sealed class CameraCredential
{
    public CameraCredential(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        UserName = userName;
        Password = password;
    }

    public string UserName { get; }

    public string Password { get; }

    public override string ToString() => $"CameraCredential {{ UserName = {UserName}, Password = *** }}";
}
