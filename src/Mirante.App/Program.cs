using Mirante.App.Services;
using Mirante.Core.Platform.Windows;
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
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Leave nothing behind that could start the app or expose camera passwords.
                StartupRegistration.Disable();
                new WindowsCredentialStore().DeleteAll();
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
