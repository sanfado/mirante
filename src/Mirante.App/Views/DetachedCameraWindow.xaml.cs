using System.Windows;
using System.Windows.Input;
using Mirante.App.Helpers;
using Mirante.App.ViewModels;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>One camera in its own window (same process), e.g. on another monitor.</summary>
internal sealed partial class DetachedCameraWindow : FluentWindow, IShortcutHost
{
    private readonly Fullscreen _fullscreen;

    internal DetachedCameraWindow(CameraTileViewModel tile)
    {
        InitializeComponent();
        DataContext = tile;
        Title = $"{tile.Name} · Mirante";
        _fullscreen = new Fullscreen(this);
        _fullscreen.Changed += (_, _) =>
        {
            TitleBar.Visibility = _fullscreen.IsActive ? Visibility.Collapsed : Visibility.Visible;
            Tile.Margin = _fullscreen.IsActive ? new Thickness(0) : new Thickness(6, 0, 6, 6);
        };
        PreviewKeyDown += (_, e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    }

    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
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
            case Key.M when DataContext is CameraTileViewModel tile:
                tile.ToggleAudioCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    public void OnTileDoubleClick(CameraTileViewModel tile) => _fullscreen.Toggle();

    internal Fullscreen Fullscreen => _fullscreen;

    /// <summary>Replaces the video host with a fresh one (see ThemeService.Changed).</summary>
    public void RecreateVideo()
    {
        var grid = (System.Windows.Controls.Grid)Tile.Parent;
        var fresh = new CameraTile { IsInDetachedWindow = true, Margin = Tile.Margin };
        System.Windows.Controls.Grid.SetRow(fresh, 1);
        grid.Children.Remove(Tile);
        grid.Children.Add(fresh);
        Tile = fresh;
    }
}
