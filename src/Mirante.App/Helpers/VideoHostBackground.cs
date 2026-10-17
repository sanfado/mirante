using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Mirante.App.Helpers;

/// <summary>
/// LibVLCSharp hosts each video in a native "static" child window. Windows asks the parent
/// window (WM_CTLCOLORSTATIC) which brush to paint it with, and the default is white —
/// visible whenever a camera has no picture. We answer with the stage's dark color.
/// </summary>
internal static class VideoHostBackground
{
    private const int WmCtlColorStatic = 0x0138;
    private static readonly IntPtr Brush = NativeMethods.CreateSolidBrush(0x00150E0A); // #0A0E15 as COLORREF (BGR)
    private static readonly HashSet<HwndSource> Hooked = [];

    /// <summary>Hooks the window that hosts <paramref name="videoView"/> and repaints its video host.</summary>
    public static void Apply(FrameworkElement videoView)
    {
        if (PresentationSource.FromVisual(videoView) is not HwndSource source)
        {
            return;
        }

        if (Hooked.Add(source))
        {
            source.AddHook(OnMessage);
            source.Disposed += (_, _) => Hooked.Remove(source);
        }

        if (FindChild<HwndHost>(videoView) is { Handle: var hwnd } && hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, true);
        }
    }

    private static IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmCtlColorStatic)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return Brush;
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static class NativeMethods
    {
        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    }
}
