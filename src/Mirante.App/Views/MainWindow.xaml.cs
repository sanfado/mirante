using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mirante.App.Helpers;
using Mirante.App.ViewModels;
using Mirante.Core.Cameras;
using Mirante.Core.Storage;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

internal sealed partial class MainWindow : FluentWindow, IShell, IShortcutHost
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsRepository _settings;
    private readonly Services.UpdateService _updates;
    private readonly Fullscreen _fullscreen;
    private readonly Dictionary<CameraTileViewModel, DetachedCameraWindow> _detached = [];
    private readonly Dictionary<Guid, WindowPlacement> _pendingPlacements = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _toastFolder;
    private bool _exiting;

    internal MainWindow(MainViewModel viewModel, SettingsRepository settings, Services.UpdateService updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _updates = updates;
        updates.PropertyChanged += (_, _) => ShowUpdateButton();
        ShowUpdateButton();
        DataContext = viewModel;
        viewModel.Shell = this;

        _fullscreen = new Fullscreen(this);
        _fullscreen.Changed += (_, _) =>
            TitleBar.Visibility = _fullscreen.IsActive ? Visibility.Collapsed : Visibility.Visible;
        _toastTimer.Tick += (_, _) => HideToast();

        _fullscreen.ApplyPlacement(settings.Load().MainWindow);
        viewModel.DefaultVolume = settings.Load().Volume ?? Mirante.Core.Cameras.AudioBoost.DefaultVolume;
        PreviewKeyDown += (_, e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
        Closing += OnClosing;
        Loaded += (_, _) => RestoreDetachedWindows();

#pragma warning disable CS8622 // WPF-UI.Tray's RoutedNotifyIconEvent has nullability attributes no handler signature satisfies.
        TrayIcon.LeftClick += OnTrayIconClick;
        TrayIcon.LeftDoubleClick += OnTrayIconClick;
#pragma warning restore CS8622
        Loaded += (_, _) => UpdateTrayIcon(settings.Load().KeepInTray); // after the icon auto-registers on load

        Services.ThemeService.Changed += RecreateVideoHosts;
        Closed += (_, _) => Services.ThemeService.Changed -= RecreateVideoHosts;
    }

    /// <summary>
    /// After a theme switch the existing video hosts no longer show video, so rebuild them:
    /// re-binding the mosaic recreates its tiles, and each tile restarts its stream on load.
    /// </summary>
    private void RecreateVideoHosts()
    {
        var active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        TilesView.ItemsSource = null;
        TilesView.ItemsSource = _viewModel.Tiles;
        foreach (var window in _detached.Values)
        {
            window.RecreateVideo();
        }

        // The new video overlays are fresh top-level windows and would sit above an open dialog
        // (e.g. Settings, where the theme was just changed); put that dialog back on top.
        if (active is not null && !ReferenceEquals(active, this))
        {
            Dispatcher.BeginInvoke(() => active.Activate(), DispatcherPriority.ApplicationIdle);
        }
    }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            ShowFromTray();
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ----- IShell -----

    public Task<CameraEditResult?> EditCameraAsync(CameraConfig? existing)
    {
        var editor = new CameraEditorWindow(existing, _viewModel.StoredCredential) { Owner = this };
        return Task.FromResult(editor.ShowDialog() == true ? editor.Result : null);
    }

    public async Task<bool> ConfirmRemoveAsync(string cameraName)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "Remover câmera",
            Content = $"Remover “{cameraName}” do Mirante?\nA senha guardada desta câmera também será apagada.",
            PrimaryButtonText = "Remover",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Cancelar",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public IReadOnlyList<Guid>? OrganizeCameras(IReadOnlyList<(Guid Id, string Name)> cameras)
    {
        var window = new OrganizeCamerasWindow(cameras) { Owner = this };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public void OpenDetached(CameraTileViewModel tile)
    {
        var window = new DetachedCameraWindow(tile);
        if (!(_pendingPlacements.Remove(tile.Id, out var placement) && window.Fullscreen.ApplyPlacement(placement)))
        {
            window.Left = Left + 60 + (_detached.Count * 32);
            window.Top = Top + 60 + (_detached.Count * 32);
        }

        window.Closed += (_, _) =>
        {
            _detached.Remove(tile);
            _viewModel.Reattach(tile);
        };
        _detached[tile] = window;
        window.Show();
    }

    public void CloseDetached(CameraTileViewModel tile)
    {
        if (_detached.TryGetValue(tile, out var window))
        {
            window.Close();
        }
    }

    public void EnterFullscreen() => _fullscreen.Enter();

    public void ShowToast(string message, string? openFolder = null)
    {
        ToastText.Text = message;
        _toastFolder = openFolder;
        ToastAction.Visibility = openFolder is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.IsHitTestVisible = true;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ----- IShortcutHost -----

    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control && key == Key.N)
        {
            _viewModel.AddCameraCommand.Execute(null);
            return true;
        }

        if (modifiers != ModifierKeys.None)
        {
            return false;
        }

        switch (key)
        {
            case Key.F11:
                _fullscreen.Toggle();
                return true;
            case Key.Escape when _fullscreen.IsActive:
                _fullscreen.Exit();
                return true;
            case Key.Escape when _viewModel.FocusedTile is not null:
                _viewModel.FocusedTile = null;
                return true;
            case Key.M when _viewModel.FocusedTile is { } focused:
                _viewModel.ToggleAudio(focused);
                return true;
            case Key.M when _viewModel.AudioTile is not null:
                _viewModel.AudioTile = null;
                return true;
            case >= Key.D0 and <= Key.D9:
                _viewModel.FocusByNumber(key - Key.D0);
                return true;
            case >= Key.NumPad0 and <= Key.NumPad9:
                _viewModel.FocusByNumber(key - Key.NumPad0);
                return true;
            default:
                return false;
        }
    }

    public void OnTileDoubleClick(CameraTileViewModel tile) => _viewModel.ToggleFocus(tile);

    // ----- UI events -----

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel.UpdateStageSize(e.NewSize.Width, e.NewSize.Height);

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => _fullscreen.Toggle();

    private void OnOpenSnapshotsClick(object sender, RoutedEventArgs e) => OpenFolder(_viewModel.SnapshotFolder());

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings, UpdateTrayIcon, _updates, ApplyUpdate) { Owner = this }.ShowDialog();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (_toastFolder is not null)
        {
            OpenFolder(_toastFolder);
        }

        HideToast();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.IsHitTestVisible = false;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
    }

    internal static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && _settings.Load().KeepInTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Remember where everything was (including detached cameras) before closing them.
        SaveLayout();
        foreach (var window in _detached.Values.ToList())
        {
            window.Close();
        }

        TrayIcon.Unregister();
    }

    internal void SaveLayout() => _settings.Save(_settings.Load() with
    {
        MainWindow = _fullscreen.CapturePlacement(),
        DetachedWindows = [.. _detached.Select(d => new DetachedWindowState(d.Key.Id, d.Value.Fullscreen.CapturePlacement()))],
    });

    /// <summary>Reopens the cameras that were in their own windows last time, where they were.</summary>
    internal void RestoreDetachedWindows()
    {
        foreach (var state in _settings.Load().DetachedWindows)
        {
            if (_viewModel.Tiles.FirstOrDefault(t => t.Id == state.CameraId) is { IsDetached: false } tile)
            {
                _pendingPlacements[tile.Id] = state.Placement;
                _viewModel.ToggleDetach(tile);
            }
        }
    }

    // ----- Updates -----

    private void ShowUpdateButton()
    {
        UpdateButton.Visibility = _updates.ReadyVersion is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.Content = $"Atualizar para {_updates.ReadyVersion}";
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => ApplyUpdate();

    /// <summary>Only on the user's click. The app exits right away, so save the layout first.</summary>
    private void ApplyUpdate()
    {
        SaveLayout();
        _updates.ApplyAndRestart();
    }

    // ----- Notification area (tray) -----

    /// <summary>Starts with only the tray icon (Windows startup with "keep in tray" on).</summary>
    public void StartInTray()
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        UpdateTrayIcon(true);
    }

    public void UpdateTrayIcon(bool visible)
    {
        if (visible && !TrayIcon.IsRegistered)
        {
            TrayIcon.Register();
        }
        else if (!visible && TrayIcon.IsRegistered)
        {
            TrayIcon.Unregister();
        }
    }

    /// <summary>Hides every window and stops decoding video; the tray icon brings it all back.</summary>
    private void HideToTray()
    {
        SaveLayout();
        foreach (var window in _detached.Values)
        {
            window.Hide();
        }

        Hide();
        _viewModel.PauseAll();
    }

    private void ShowFromTray()
    {
        Show();
        foreach (var window in _detached.Values)
        {
            window.Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _viewModel.ResumeAll();
    }

    /// <summary>Really quits, even with "keep in tray" on.</summary>
    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void OnTrayIconClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayExitClick(object sender, RoutedEventArgs e) => ExitApp();
}
