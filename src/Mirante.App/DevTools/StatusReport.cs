#if DEBUG
using System.Windows;
using Mirante.App.ViewModels;

namespace Mirante.App.DevTools;

/// <summary>
/// Debug builds only: <c>Mirante.exe --status-report &lt;file&gt;</c> opens the real cameras,
/// waits, writes each one's status and whether a snapshot works, then exits. No image is kept
/// (snapshots are measured and deleted), so it can check a real home camera privately.
/// </summary>
internal static class StatusReport
{
    public static bool TryParse(string[] args, out string file)
    {
        var i = Array.IndexOf(args, "--status-report");
        file = i >= 0 && args.Length > i + 1 ? args[i + 1] : string.Empty;
        return file.Length > 0;
    }

    public static async Task RunAsync(MainViewModel viewModel, string file)
    {
        await Task.Delay(15000);
        var folder = Directory.CreateTempSubdirectory("mirante-status-").FullName;
        var lines = Describe(viewModel, folder, keepSnapshots: false).ToList();

        // Volume boost + "Realçar voz" on the real stream, with the camera MUTED (silent) and
        // restored afterwards without saving.
        foreach (var (tile, i) in viewModel.Tiles.Select((t, i) => (t, i)))
        {
            var original = tile.Stream.Config;
            tile.Stream.UpdateAudioSettings(original with { Volume = 180, VoiceBoost = true });
            await Task.Delay(3000);
            lines.Add($"câmera {i + 1}: 180% + realçar voz (muda): {tile.Stream.AudioState} status={tile.Status}");
            tile.Stream.UpdateAudioSettings(original);
        }

        await Task.Delay(1000);
        File.WriteAllLines(file, lines);
        Directory.Delete(folder, recursive: true);
        Application.Current.Shutdown();
    }

    /// <summary>One line per camera: status and snapshot result (size of the PNG written).</summary>
    public static IEnumerable<string> Describe(MainViewModel viewModel, string snapshotFolder, bool keepSnapshots = true)
    {
        var lines = new List<string>();
        foreach (var (tile, i) in viewModel.Tiles.Select((t, i) => (t, i)))
        {
            var path = Path.Combine(snapshotFolder, $"foto-{i + 1}.png");
            var accepted = tile.Stream.TakeSnapshot(path);
            Thread.Sleep(1500); // LibVLC writes the snapshot asynchronously
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (!keepSnapshots && File.Exists(path))
            {
                File.Delete(path);
            }

            // AudioTrackCount includes VLC's "disabled" entry; decoding happens even while muted.
            var player = tile.Stream.Player;
            var audio = player.AudioTrackCount > 1
                ? $"sim ({player.Media?.Statistics.DecodedAudio ?? 0} blocos decodificados, mudo={player.Mute})"
                : "câmera não envia áudio";
            lines.Add($"câmera {i + 1}: status={tile.Status} foto={(accepted && size > 0 ? $"ok ({size / 1024} KB)" : "falhou")} áudio={audio}");
        }

        return lines;
    }
}
#endif
