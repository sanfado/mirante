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

        var cameras = new CameraRepository(Path.Combine(data, "cameras.json"));
        cameras.Save(configs);
        engine.DemoMedia = config => new Media(engine.LibVlc, new Uri(clips[configs.FindIndex(c => c.Id == config.Id)]),
            ":input-repeat=65535", ":no-audio");
        return (cameras, new SettingsRepository(Path.Combine(data, "settings.json")), credentials);
    }

    public static async Task RunAsync(MainWindow window, MainViewModel viewModel, SettingsRepository settings, string output)
    {
        Directory.CreateDirectory(output);
        await Task.Delay(7000);
        Capture(window, output, "01-mosaico");
        if (viewModel.Tiles.Count == 0)
        {
            Application.Current.Shutdown();
            return;
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
        File.WriteAllLines(Path.Combine(output, "relatorio.txt"), report);

        Application.Current.Shutdown();
    }

    private static string Bounds(Window w) => $"({w.Left:0},{w.Top:0}) {w.Width:0}x{w.Height:0}";

    private static void Capture(Window window, string folder, string name)
    {
        window.Activate();
        CaptureScreenArea(window, folder, name);
    }

    /// <summary>Captures the screen where <paramref name="window"/> is, without bringing it to the front.</summary>
    private static void CaptureScreenArea(Window window, string folder, string name)
    {
        window.UpdateLayout();
        var hwnd = new WindowInteropHelper(window).Handle;
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
