using Velopack;

namespace Mirante.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run first: lets the installer call into the app (install/update/uninstall hooks)
        // and exits right away when it was only launched for that.
        VelopackApp.Build()
            // A downloaded update waits for the user's click; never apply it silently on startup.
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
            {
#if !DEBUG
                // Leave nothing behind that could start the app or expose camera passwords.
                // (Debug test installs share the Run entry and credentials with the real app:
                // uninstalling one of those must not wipe them.)
                Services.StartupRegistration.Disable();
                new Mirante.Core.Platform.Windows.WindowsCredentialStore().DeleteAll();
#endif
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
