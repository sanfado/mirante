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

        // Pan/tilt found over ONVIF (read-only: nothing is moved here).
        foreach (var (tile, i) in viewModel.Tiles.Select((t, i) => (t, i)))
        {
            lines.Add($"câmera {i + 1}: mover câmera (PTZ) disponível={tile.Ptz.IsAvailable} diagonal={tile.Ptz.SupportsDiagonal} zoom={tile.Ptz.SupportsZoom}");
        }

        // Recording, with MIRANTE_RECORD_TEST=<folder>: files go there (never the user's folder) and the
        // caller probes and deletes them. Nothing is shown.
        if (Environment.GetEnvironmentVariable("MIRANTE_RECORD_TEST") is { Length: > 0 } recordFolder
            && viewModel.Tiles.FirstOrDefault() is { } camera)
        {
            viewModel.RecordingFolder = () => recordFolder;
            var recorder = camera.Recorder;
            void Step(string what) => lines.Add(
                $"gravação {what}: estado={recorder.State} selo=\"{recorder.Badge}\" problema={recorder.Problem ?? "-"} vídeo={camera.Status} arquivos={Files()}");
            string Files() => string.Join(", ", Directory.EnumerateFiles(recordFolder, "*.mkv", SearchOption.AllDirectories)
                .Select(f => $"{Path.GetFileName(f)}={new FileInfo(f).Length / 1024}KB"));

            recorder.ToggleManual();
            await Task.Delay(12000);
            Step("manual 12 s");
            recorder.SetContinuous(true);
            await Task.Delay(2000);
            recorder.ToggleManual(); // manual -> continuous: a switch with 2 connections already in use
            await Task.Delay(16000);
            Step("manual -> contínua");
            recorder.Restart();
            await Task.Delay(16000);
            Step("reinício");
            recorder.SetContinuous(false);
            await Task.Delay(4000);
            Step("desligada");
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
