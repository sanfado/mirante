using System.Runtime.InteropServices;
using Wpf.Ui.Tray.Controls;

namespace Mirante.App.Helpers;

/// <summary>
/// A notification from the tray icon (shown by Windows as a regular notification). WPF-UI's
/// NotifyIcon has no API for it, but exposes the window and id it registered the icon with.
/// </summary>
internal static class TrayBalloon
{
    private const uint NimModify = 1;
    private const uint NifInfo = 0x10;
    private const uint NiifWarning = 2;

    public static bool Show(NotifyIcon icon, string title, string text)
    {
        if (!icon.IsRegistered || icon.HookWindow is not { } hook)
        {
            return false;
        }

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = hook.Handle,
            Id = (uint)icon.Id,
            Flags = NifInfo,
            Info = Truncate(text, 255),
            InfoTitle = Truncate(title, 63),
            InfoFlags = NiifWarning,
        };
        return NativeMethods.Shell_NotifyIconW(NimModify, ref data);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    }
}
