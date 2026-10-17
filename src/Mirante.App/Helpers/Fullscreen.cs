using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Mirante.Core.Storage;

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

    /// <summary>The window's normal bounds and state (even while fullscreen), to save on exit.</summary>
    public WindowPlacement CapturePlacement()
    {
        var bounds = IsActive ? _restoreBounds
            : window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var maximized = IsActive ? _restoreState == WindowState.Maximized : window.WindowState == WindowState.Maximized;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized, IsActive);
    }

    /// <summary>
    /// Applies a saved placement before the window is shown, including going fullscreen on the
    /// same monitor. Returns false (and changes nothing) if it would land off-screen, e.g. after
    /// a monitor was unplugged.
    /// </summary>
    public bool ApplyPlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < window.MinWidth || placement.Height < window.MinHeight)
        {
            return false;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!screen.IntersectsWith(saved))
        {
            return false;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;
        if (placement.IsFullscreen)
        {
            void EnterOnce(object? sender, RoutedEventArgs e)
            {
                window.Loaded -= EnterOnce;
                if (placement.IsMaximized)
                {
                    window.WindowState = WindowState.Maximized;
                }

                Enter();
            }

            window.Loaded += EnterOnce;
        }
        else if (placement.IsMaximized)
        {
            window.SourceInitialized += (_, _) => window.WindowState = WindowState.Maximized;
        }

        return true;
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
