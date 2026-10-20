#if DEBUG
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Mirante.App.ViewModels;
using Mirante.App.Views;
using Mirante.Core.Cameras;
using Mirante.Core.Security;
using Mirante.Core.Storage;

namespace Mirante.App.DevTools;

/// <summary>
/// Debug builds only: <c>Mirante.exe --demo &lt;videos&gt; &lt;screenshots&gt;</c> plays local
/// clips instead of real cameras, walks through the main screens and saves screenshots.
/// Uses a throwaway data folder and in-memory credentials, never the user's real ones.
/// </summary>
internal static class DemoMode
{
    private static readonly string[] Names = ["Sala", "Cozinha", "Garagem", "Quintal", "Entrada", "Varanda"];

    public static bool TryParse(string[] args, out string videos, out string output)
    {
        var i = Array.IndexOf(args, "--demo");
        videos = i >= 0 && args.Length > i + 2 ? args[i + 1] : string.Empty;
        output = i >= 0 && args.Length > i + 2 ? args[i + 2] : string.Empty;
        return videos.Length > 0;
    }

    public static (CameraRepository Cameras, SettingsRepository Settings, ICredentialStore Credentials) Setup(
        string videos, Video.VideoEngine engine)
    {
        var data = Directory.CreateTempSubdirectory("mirante-demo-").FullName;

        var clips = Directory.GetFiles(videos, "*.mp4").Order().ToArray();
        var credentials = new InMemoryCredentialStore();
        var configs = clips.Select((_, i) => CameraConfig.CreateNew(Names[i % Names.Length], $"192.168.1.{100 + i}")).ToList();
        foreach (var config in configs.SkipLast(1))
        {
            credentials.Write(config.Id, new CameraCredential("demo", "demo"));
        }

        // A camera that never connects (like a typo'd IP): keeps reconnecting until removed.
        var ghost = CameraConfig.CreateNew("Fantasma", "192.0.2.1");
        configs.Add(ghost);
        credentials.Write(ghost.Id, new CameraCredential("demo", "demo"));

        var cameras = new CameraRepository(Path.Combine(data, "cameras.json"));
        cameras.Save(configs);
        engine.DemoMedia = config => config.Id == ghost.Id
            ? new Media(engine.LibVlc, new Uri(Path.Combine(data, "nao-existe.mp4")))
            : new Media(engine.LibVlc, new Uri(clips[configs.FindIndex(c => c.Id == config.Id)]), ":input-repeat=65535");
        return (cameras, new SettingsRepository(Path.Combine(data, "settings.json")), credentials);
    }

    public static async Task RunAsync(MainWindow window, MainViewModel viewModel, SettingsRepository settings, string output)
    {
        Directory.CreateDirectory(output);
        // Never the user's real recordings folder.
        var recordings = Directory.CreateDirectory(Path.Combine(output, "gravacoes")).FullName;
        viewModel.RecordingFolder = () => recordings;
        await Task.Delay(7000);
        Capture(window, output, "01-mosaico");
        File.WriteAllLines(Path.Combine(output, "00-status-e-fotos.txt"), StatusReport.Describe(viewModel, output));
        // Audio: only one camera plays at a time, volume follows the slider. Kept quiet on purpose.
        var audio = new List<string>();
        void AudioState(string step)
        {
            audio.Add($"-- {step}");
            foreach (var tile in viewModel.Tiles)
            {
                var player = tile.Stream.Player;
                audio.Add($"   {tile.Name,-8} somLigado={tile.IsAudioOn,-5} mudo={player.Mute,-5} volume={player.Volume,3} " +
                          $"faixasDeAudio={player.AudioTrackCount} amostrasDecodificadas={player.Media?.Statistics.DecodedAudio ?? 0}");
            }
        }

        // Silent on purpose (may run while someone sleeps nearby): every camera at 0%.
        foreach (var tile in viewModel.Tiles)
        {
            tile.Stream.UpdateAudioSettings(tile.Stream.Config with { Volume = 0 });
        }

        await Task.Delay(2000);
        AudioState("início (todas mudas)");
        viewModel.ToggleAudio(viewModel.Tiles[0]);
        await Task.Delay(2500);
        AudioState($"som em {viewModel.Tiles[0].Name}");
        if (viewModel.Tiles.Count > 1)
        {
            viewModel.ToggleAudio(viewModel.Tiles[1]);
            await Task.Delay(2500);
            AudioState($"som em {viewModel.Tiles[1].Name} (a anterior deve silenciar)");
            RenderElement(window.TitleBar, output, "01b-barra-com-som");
            viewModel.Volume = 0;
            await Task.Delay(800);
            AudioState("volume 0");
            viewModel.ToggleAudio(viewModel.Tiles[1]);
            await Task.Delay(800);
            AudioState("som desligado");
        }

        // Volume above 100% and "Realçar voz", tested on a MUTED camera (no sound at all):
        // the player stays at 100 and the rest becomes equalizer pre-amplification, live.
        if (viewModel.Tiles.Count > 2)
        {
            var boosted = viewModel.Tiles[2];
            boosted.Stream.UpdateAudioSettings(boosted.Stream.Config with { Volume = 180, VoiceBoost = true });
            await Task.Delay(1500);
            audio.Add($"-- {boosted.Name} muda, volume 180% + realçar voz: {boosted.Stream.AudioState} status={boosted.Status}");
            boosted.Stream.UpdateAudioSettings(boosted.Stream.Config with { Volume = 100, VoiceBoost = false });
            await Task.Delay(1500);
            audio.Add($"-- {boosted.Name} de volta a 100% sem realce: {boosted.Stream.AudioState} status={boosted.Status}");
        }

        // The controls follow the camera that plays (its own saved settings), still silent.
        viewModel.ToggleAudio(viewModel.Tiles[0]);
        viewModel.VoiceBoost = true;
        await Task.Delay(1500);
        audio.Add($"-- realçar voz pela barra em {viewModel.Tiles[0].Name}: config.voz={viewModel.Tiles[0].Stream.Config.VoiceBoost} {viewModel.Tiles[0].Stream.AudioState} status={viewModel.Tiles[0].Status}");
        RenderElement(window.TitleBar, output, "01b-barra-realcar-voz");
        viewModel.VoiceBoost = false;
        viewModel.AudioTile = null;
        await Task.Delay(3000);

        File.WriteAllLines(Path.Combine(output, "00-audio.txt"), audio);

        // Organize cameras: the dialog, then a reversed order applied to the mosaic.
        var organize = new OrganizeCamerasWindow(viewModel.Tiles.Select(t => (t.Id, t.Name))) { Owner = window };
        organize.Show();
        await Task.Delay(1000);
        RenderElement((FrameworkElement)organize.Content, output, "01c-organizar");
        organize.Close();
        var before = string.Join(", ", viewModel.Tiles.Select(t => t.Name));
        viewModel.ApplyOrder([.. viewModel.Tiles.Select(t => t.Id).Reverse()]);
        await Task.Delay(7000);
        File.WriteAllLines(Path.Combine(output, "00-ordem.txt"),
        [
            $"antes:  {before}",
            $"depois: {string.Join(", ", viewModel.Tiles.Select(t => $"{t.Name}={t.Status}"))}",
        ]);
        Capture(window, output, "01d-ordem-invertida");

        // Pan/tilt arrows (demo videos have no PTZ, so only the look is checked; WPF-only render).
        if (viewModel.Tiles.FirstOrDefault(t => t.Status == Video.StreamStatus.Live) is { } live
            && FindTile(window, live) is { } tileView)
        {
            live.IsPtzMode = true;
            await Task.Delay(800);
            RenderElement(tileView.Overlay, output, "01e-setas-ptz");
            live.Ptz.SimulateForDemo(); // a camera with zoom
            await Task.Delay(500);
            RenderElement(tileView.Overlay, output, "01f-setas-ptz-com-zoom");
            var nameDialog = new NameWindow("Salvar posição", $"Guarda para onde “{live.Name}” está apontando agora.",
                "Ex.: Porta, Sofá, Janela", "Posição 1") { Owner = window };
            nameDialog.Show();
            await Task.Delay(800);
            RenderElement((FrameworkElement)nameDialog.Content, output, "01g-salvar-posicao");
            nameDialog.Close();
            live.IsPtzMode = false;
            live.Ptz.Reset();
        }

        if (viewModel.Tiles.Count == 0)
        {
            Application.Current.Shutdown();
            return;
        }

        // Layouts: save "Noite" (one camera left out, another in focus), mess up the screen, apply it
        // back with Ctrl+1, then show every camera again.
        if (viewModel.Tiles.Count > 2)
        {
            var log = new List<string>();
            var leftOut = viewModel.Tiles[^1];
            viewModel.FocusByNumber(1);
            var focused = viewModel.FocusedTile;
            var noite = await window.CaptureLayoutAsync(Guid.NewGuid(), "Noite", new HashSet<Guid> { leftOut.Id }, includePositions: false);
            window.StoreLayout(noite);
            viewModel.FocusByNumber(0);
            viewModel.ToggleDetach(viewModel.Tiles[1]);
            await Task.Delay(2500);
            log.Add($"antes: foco={viewModel.FocusedTile?.Name ?? "-"} destacadas={viewModel.DetachedTiles.Count}");
            log.Add($"Ctrl+1 tratado={window.HandleShortcut(Key.D1, ModifierKeys.Control)}");
            await Task.Delay(3500);
            log.Add($"depois: foco={viewModel.FocusedTile?.Name ?? "-"} (esperado {focused?.Name})");
            log.Add($"destacadas={viewModel.DetachedTiles.Count} (esperado 0)");
            log.Add($"fora do layout: {leftOut.Name} excluída={leftOut.IsExcluded} status={leftOut.Status} (esperado Idle) chip=\"{viewModel.HiddenLabel}\"");
            log.Add($"no mosaico: {string.Join(", ", viewModel.Tiles.Where(t => t.IsVisibleInGrid).Select(t => $"{t.Name}={t.Status}"))}");
            log.Add($"salvo: {string.Join(", ", settings.Load().Layouts.Select(l => l.Name))}");
            RenderElement(window.TitleBar, output, "01h-barra-layouts");

            // REC badge on a camera being recorded (demo clips: the file is just copied).
            var recorded = viewModel.Tiles.First(t => t.IsVisibleInGrid);
            viewModel.ToggleRecording(recorded);
            await Task.Delay(2500);
            if (FindTile(window, recorded) is { } recordedView)
            {
                RenderElement(recordedView.Overlay, output, "01j-gravando");
            }

            log.Add($"gravando: estado={recorded.Recorder.State} selo=\"{recorded.Recorder.Badge}\"");
            viewModel.ToggleRecording(recorded);
            await Task.Delay(2000);
            log.Add($"parada: estado={recorded.Recorder.State} arquivos={Directory.EnumerateFiles(recordings, "*.mkv", SearchOption.AllDirectories).Count()}");
            viewModel.FocusByNumber(0);
            viewModel.SetExcluded([]);
            await Task.Delay(5000);
            log.Add($"mostrar todas: {leftOut.Name} excluída={leftOut.IsExcluded} status={leftOut.Status} chip visível={viewModel.HasHiddenCameras}");
            File.WriteAllLines(Path.Combine(output, "00-layouts.txt"), log);

            var saveDialog = new SaveLayoutWindow(viewModel.Tiles.Select(t => (t.Id, t.Name, t != leftOut)), "Layout 2", anyPtz: true) { Owner = window };
            saveDialog.Show();
            await Task.Delay(800);
            RenderElement((FrameworkElement)saveDialog.Content, output, "01i-salvar-layout");
            saveDialog.Close();
        }

        viewModel.FocusByNumber(1);
        await Task.Delay(1500);
        Capture(window, output, "02-foco");
        viewModel.FocusByNumber(0);

        if (viewModel.Tiles.Count > 2)
        {
            viewModel.ToggleDetach(viewModel.Tiles[1]);
            await Task.Delay(5000);
            Capture(window, output, "03-mosaico-com-destacada");
            if (Application.Current.Windows.OfType<DetachedCameraWindow>().FirstOrDefault() is { } detached)
            {
                Capture(detached, output, "04-janela-destacada");
                detached.Close();
                await Task.Delay(3000);
            }
        }

        window.Activate();
        window.EnterFullscreen();
        await Task.Delay(2500);
        Capture(window, output, "05-tela-cheia");
        window.HandleShortcut(Key.Escape, ModifierKeys.None);
        await Task.Delay(1500);

        var editor = new CameraEditorWindow(null, _ => null) { Owner = window };
        editor.Show();
        await Task.Delay(1200);
        Capture(editor, output, "06-adicionar-camera");
        editor.Close();

        var settingsWindow = new SettingsWindow(settings, window.UpdateTrayIcon,
            new Services.UpdateService(window.Dispatcher), () => { }) { Owner = window };
        settingsWindow.Show();
        await Task.Delay(1200);
        Capture(settingsWindow, output, "07-configuracoes");
        RenderElement((FrameworkElement)settingsWindow.Content, output, "07b-configuracoes-render");

        // Same sequence that once left the window half light, half dark: System → Light → Dark.
        foreach (var (theme, name) in new[] { (AppTheme.System, "08-tema-sistema"), (AppTheme.Light, "09-tema-claro"), (AppTheme.Dark, "10-tema-escuro") })
        {
            settingsWindow.Activate(); // like a user changing the theme from the Settings window
            Services.ThemeService.Apply(theme);
            await Task.Delay(3000);
            CaptureScreenArea(window, output, name);
            if (theme == AppTheme.Light)
            {
                // A window opened after the switch must follow the new theme too.
                var lateEditor = new CameraEditorWindow(null, _ => null) { Owner = window };
                lateEditor.Show();
                await Task.Delay(1200);
                Capture(lateEditor, output, "09b-editor-aberto-depois");
                lateEditor.Close();
            }
        }

        settingsWindow.Close();
        var report = new List<string>();

        // Detached camera remembered across restarts: other monitor + fullscreen.
        if (viewModel.Tiles.Count > 2)
        {
            viewModel.ToggleDetach(viewModel.Tiles[2]);
            await Task.Delay(3000);
            var detached = Application.Current.Windows.OfType<DetachedCameraWindow>().First();
            detached.Left = SystemParameters.VirtualScreenLeft + 200;
            detached.Top = SystemParameters.VirtualScreenTop + 200;
            detached.Fullscreen.Enter();
            await Task.Delay(2000);
            report.Add($"antes:   {Bounds(detached)} fullscreen={detached.Fullscreen.IsActive}");
            window.SaveLayout();
            detached.Close();
            await Task.Delay(2000);
            window.RestoreDetachedWindows();
            await Task.Delay(4000);
            var restored = Application.Current.Windows.OfType<DetachedCameraWindow>().First();
            report.Add($"depois:  {Bounds(restored)} fullscreen={restored.Fullscreen.IsActive}");
            Capture(restored, output, "11-destacada-restaurada");
            restored.Close();
            await Task.Delay(2000);
        }

        // Close to the tray and come back.
        settings.Save(settings.Load() with { KeepInTray = true });
        window.UpdateTrayIcon(true);
        window.Close();
        await Task.Delay(1500);
        report.Add($"bandeja: janela visível={window.IsVisible} ícone={window.TrayIcon.IsRegistered} status={viewModel.Tiles[0].Status}");
        window.BringToFront();
        await Task.Delay(5000);
        report.Add($"voltou:  janela visível={window.IsVisible} status={viewModel.Tiles[0].Status}");
        Capture(window, output, "12-voltou-da-bandeja");
        settings.Save(settings.Load() with { KeepInTray = false });
        window.UpdateTrayIcon(false);
        report.Add($"bandeja desligada: ícone={window.TrayIcon.IsRegistered}");

        // Removing cameras in every state must never take the app down (it once crashed here).
        if (viewModel.Tiles.FirstOrDefault(t => t.Status == Video.StreamStatus.Live) is { } detachTarget)
        {
            viewModel.ToggleDetach(detachTarget);
            await Task.Delay(3000);
        }

        var victims = viewModel.Tiles
            .Where(t => t.Name == "Fantasma" || t.IsDetached || t.Status == Video.StreamStatus.MissingCredentials)
            .Concat(viewModel.Tiles.Where(t => t.Status == Video.StreamStatus.Live && !t.IsDetached).Take(1))
            .Distinct().ToList();
        var states = string.Join(", ", victims.Select(t => $"{t.Name}={(t.IsDetached ? "Destacada" : t.Status.ToString())}"));
        foreach (var victim in victims)
        {
            viewModel.RemoveCamera(victim);
        }

        await Task.Delay(6000);
        report.Add($"removidas: {states}");
        report.Add($"app de pé após remover; restantes: {string.Join(", ", viewModel.Tiles.Select(t => $"{t.Name}={t.Status}"))}");
        File.WriteAllLines(Path.Combine(output, "relatorio.txt"), report);

        Application.Current.Shutdown();
    }

    private static string Bounds(Window w) => $"({w.Left:0},{w.Top:0}) {w.Width:0}x{w.Height:0}";

    private static void Capture(Window window, string folder, string name)
    {
        window.Activate();
        CaptureScreenArea(window, folder, name);
    }

    /// <summary>
    /// Captures the screen where <paramref name="window"/> is. Skipped unless one of our windows is in
    /// the foreground: otherwise it would photograph whatever the user has open on top of it.
    /// </summary>
    private static void CaptureScreenArea(Window window, string folder, string name)
    {
        window.UpdateLayout();
        var hwnd = new WindowInteropHelper(window).Handle;
        var foreground = NativeMethods.GetForegroundWindow();
        if (!Application.Current.Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == foreground))
        {
            File.WriteAllText(Path.Combine(folder, name + ".pulada.txt"), "Janela não estava em primeiro plano; captura pulada.");
            return;
        }

        _ = NativeMethods.DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<NativeMethods.Rect>());
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var memory = NativeMethods.CreateCompatibleDC(screen);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
        var old = NativeMethods.SelectObject(memory, bitmap);
        NativeMethods.BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
        NativeMethods.SelectObject(memory, old);
        var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        NativeMethods.DeleteObject(bitmap);
        NativeMethods.DeleteDC(memory);
        _ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private static CameraTile? FindTile(System.Windows.DependencyObject root, ViewModels.CameraTileViewModel tile)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is CameraTile view && ReferenceEquals(view.DataContext, tile))
            {
                return view;
            }

            if (FindTile(child, tile) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Renders WPF content only (no screen grab, no video): safe while the user uses the PC.</summary>
    private static void RenderElement(FrameworkElement element, string folder, string name)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);

        // Mica and the video are not WPF content: paint the dark theme's background behind the element
        // so light text stays readable in the picture.
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), null, bounds);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(element) { Stretch = System.Windows.Media.Stretch.None }, null, bounds);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<Guid, CameraCredential> _items = [];

        public CameraCredential? Read(Guid cameraId) => _items.GetValueOrDefault(cameraId);

        public void Write(Guid cameraId, CameraCredential credential) => _items[cameraId] = credential;

        public void Delete(Guid cameraId) => _items.Remove(cameraId);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);
    }
}
#endif
