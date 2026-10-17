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
    private readonly Fullscreen _fullscreen;
    private readonly Dictionary<CameraTileViewModel, DetachedCameraWindow> _detached = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _toastFolder;

    internal MainWindow(MainViewModel viewModel, SettingsRepository settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        viewModel.Shell = this;

        _fullscreen = new Fullscreen(this);
        _fullscreen.Changed += (_, _) =>
            TitleBar.Visibility = _fullscreen.IsActive ? Visibility.Collapsed : Visibility.Visible;
        _toastTimer.Tick += (_, _) => HideToast();

        RestorePlacement(settings.Load().MainWindow);
        PreviewKeyDown += (_, e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
        Closing += OnClosing;

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

    public void OpenDetached(CameraTileViewModel tile)
    {
        var window = new DetachedCameraWindow(tile)
        {
            Left = Left + 60 + (_detached.Count * 32),
            Top = Top + 60 + (_detached.Count * 32),
        };
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
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
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
        _fullscreen.Exit();
        foreach (var window in _detached.Values.ToList())
        {
            window.Close();
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Save(_settings.Load() with
        {
            MainWindow = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                WindowState == WindowState.Maximized),
        });
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        // Only restore if the window would still be visible (a monitor may have been unplugged).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!screen.IntersectsWith(saved))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
    }
}
