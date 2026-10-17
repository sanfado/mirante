#if DEBUG
using System.Windows;
using Mirante.App.Services;

namespace Mirante.App.DevTools;

/// <summary>
/// Debug builds only. With MIRANTE_UPDATE_SOURCE (local releases folder) and
/// MIRANTE_UPDATE_SELFTEST (log file) set, an installed test build checks for an update,
/// applies it, and the restarted version logs itself — the full flow without clicking.
/// </summary>
internal static class UpdateSelfTest
{
    public static async Task RunAsync(UpdateService updates)
    {
        if (Environment.GetEnvironmentVariable("MIRANTE_UPDATE_SELFTEST") is not { Length: > 0 } log)
        {
            return;
        }

        await Task.Delay(3000);
        var result = await updates.CheckAsync();
        await File.AppendAllLinesAsync(log,
            [$"{DateTime.Now:HH:mm:ss} versão={updates.CurrentVersion} resultado={result} pronta={updates.ReadyVersion} erro={updates.LastError}"]);
        // "download" mode: stop after downloading, to prove the next start does NOT apply it by itself.
        var downloadOnly = Environment.GetEnvironmentVariable("MIRANTE_UPDATE_SELFTEST_MODE") == "download";
        if (result == UpdateCheckResult.Ready && !downloadOnly)
        {
            updates.ApplyAndRestart();
            return;
        }

        Application.Current.Shutdown();
    }
}
#endif
