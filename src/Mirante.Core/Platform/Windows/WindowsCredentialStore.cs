using System.ComponentModel;
using System.Runtime.InteropServices;
using Mirante.Core.Security;

namespace Mirante.Core.Platform.Windows;

/// <summary>
/// Stores camera credentials in the Windows Credential Manager (generic credentials).
/// Windows encrypts them with the user's logon secret (DPAPI); they never touch our
/// config files. Persist = LOCAL_MACHINE keeps them on this PC (no roaming).
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    public const string DefaultPrefix = "Mirante/camera/";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxPasswordChars = 256;

    private readonly string _prefix;

    public WindowsCredentialStore(string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        _prefix = prefix;
    }

    public CameraCredential? Read(Guid cameraId)
    {
        if (!NativeMethods.CredRead(TargetName(cameraId), CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            var userName = Marshal.PtrToStringUni(credential.UserName);
            if (string.IsNullOrEmpty(userName))
            {
                return null;
            }

            var password = credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return new CameraCredential(userName, password);
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    public void Write(Guid cameraId, CameraCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Password.Length > MaxPasswordChars)
        {
            throw new ArgumentException("Password too long.", nameof(credential));
        }

        var target = Marshal.StringToCoTaskMemUni(TargetName(cameraId));
        var user = Marshal.StringToCoTaskMemUni(credential.UserName);
        var blob = Marshal.StringToCoTaskMemUni(credential.Password);
        try
        {
            var native = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(credential.Password.Length * 2),
                Persist = CredPersistLocalMachine,
            };
            if (!NativeMethods.CredWrite(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.ZeroFreeCoTaskMemUnicode(blob); // wipe the password copy
        }
    }

    public void Delete(Guid cameraId)
    {
        if (!NativeMethods.CredDelete(TargetName(cameraId), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private string TargetName(Guid cameraId) => _prefix + cameraId.ToString("N");

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void CredFree(IntPtr buffer);
    }
}
