using Microsoft.Win32;

namespace Mirante.App.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, no scheduled task).</summary>
internal static class StartupRegistration
{
    public const string StartupArgument = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Mirante";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, CommandLine);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If enabled from another copy of the app (e.g. a dev build), point it to this one.</summary>
    public static void RefreshPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && current != CommandLine)
        {
            Enable();
        }
    }

    private static string CommandLine => $"\"{Environment.ProcessPath}\" {StartupArgument}";
}
