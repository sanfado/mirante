using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mirante.App.Helpers;

/// <summary>
/// Borderless fullscreen on the monitor that currently holds the window
/// (WPF's own approaches tend to jump to the primary monitor).
/// </summary>
internal sealed class Fullscreen(Window window)
{
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private ResizeMode _restoreResizeMode;

    public bool IsActive { get; private set; }

    public event EventHandler? Changed;

    public void Toggle()
    {
        if (IsActive)
        {
            Exit();
        }
        else
        {
            Enter();
        }
    }

    public void Enter()
    {
        if (IsActive)
        {
            return;
        }

        var monitor = CurrentMonitorBounds();
        _restoreState = window.WindowState;
        _restoreResizeMode = window.ResizeMode;
        _restoreBounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        IsActive = true;
        window.WindowState = WindowState.Normal;
        window.ResizeMode = ResizeMode.NoResize;
        window.Left = monitor.Left;
        window.Top = monitor.Top;
        window.Width = monitor.Width;
        window.Height = monitor.Height;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Exit()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        window.ResizeMode = _restoreResizeMode;
        window.Left = _restoreBounds.Left;
        window.Top = _restoreBounds.Top;
        window.Width = _restoreBounds.Width;
        window.Height = _restoreBounds.Height;
        window.WindowState = _restoreState;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Rect CurrentMonitorBounds()
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
