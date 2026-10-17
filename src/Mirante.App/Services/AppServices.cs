using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Mirante.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Mirante.App.Services;

/// <summary>
/// Makes sure only one Mirante runs per user session. A second launch just brings the
/// existing window to the front.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Id = "Mirante-3f6d0c52-6b8e-4c1f-9a77-2d6c1b9e8a41";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out var createdNew);
        IsFirst = createdNew;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Id}-activate");
    }

    public bool IsFirst { get; }

    public void ActivateFirstInstance() => _activate.Set();

    public void ListenForActivation(Action onActivate) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null,
            Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirst)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _activate.Dispose();
    }
}

internal static class CrashLog
{
    public static string? Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogFolder);
            var path = Path.Combine(AppPaths.LogFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"Mirante {AppInfo.Version}\n{DateTime.Now:O}\n\n{exception}");
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}

internal static class ThemeService
{
    // Brand blue as explicit Windows 11 accent ramps (system, primary, secondary, tertiary):
    // darker shades on light backgrounds, lighter ones on dark, so button text stays readable.
    // Letting the library derive the ramp gave washed-out buttons in the light theme.
    private static readonly System.Windows.Media.Color[] LightRamp = [Rgb(0x2B6FE6), Rgb(0x1F5FD6), Rgb(0x1A4FB3), Rgb(0x163F8F)];
    private static readonly System.Windows.Media.Color[] DarkRamp = [Rgb(0x5B9BFF), Rgb(0x7DB2FF), Rgb(0x9DC4FF), Rgb(0xBDD7FF)];

    private static System.Windows.Media.Color Rgb(int rgb) =>
        System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static void Apply(AppTheme theme)
    {
        var resolved = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => SystemUsesLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        // Accent first: the theme dictionary builds its accent brushes from the accent colors
        // present when it loads, so applying it afterwards left buttons one theme behind.
        var ramp = resolved == ApplicationTheme.Light ? LightRamp : DarkRamp;
        ApplicationAccentColorManager.Apply(ramp[0], ramp[1], ramp[2], ramp[3]);
        ApplicationThemeManager.Apply(resolved, WindowBackdropType.Mica, updateAccent: false);

        // Apply() swaps the colors but leaves secondary windows' Mica in the old light/dark variant
        // (light background behind dark-theme text). Only our own windows: LibVLCSharp's
        // transparent video overlays must stay untouched.
        foreach (var window in Application.Current.Windows.OfType<FluentWindow>())
        {
            WindowBackgroundManager.UpdateBackground(window, resolved, WindowBackdropType.Mica);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Raised after a theme switch while the app runs. Re-creating a window's backdrop stops Windows
    /// from compositing the video surfaces already inside it (cameras go blank, even if restarted),
    /// so the views must re-create their video hosts.
    /// </summary>
    public static event Action? Changed;

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
