using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Mirante.App.ViewModels;

namespace Mirante.App.Views;

/// <summary>Something that handles keyboard shortcuts typed while the video overlay has focus.</summary>
internal interface IShortcutHost
{
    bool HandleShortcut(Key key, ModifierKeys modifiers);

    void OnTileDoubleClick(CameraTileViewModel tile);
}

internal sealed partial class CameraTile : UserControl
{
    public static readonly DependencyProperty IsInDetachedWindowProperty = DependencyProperty.Register(
        nameof(IsInDetachedWindow), typeof(bool), typeof(CameraTile), new PropertyMetadata(false, OnModeChanged));

    private readonly System.Windows.Threading.DispatcherTimer _repaint;
    private CameraTileViewModel? _tile;
    private (uint Width, uint Height)? _videoSize;
    private Key? _ptzKey;

    public CameraTile()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateAttachment();
            Dispatcher.BeginInvoke(() => Helpers.VideoHostBackground.Apply(Video), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) => _tile?.Stream.Release(Video);
        DataContextChanged += OnDataContextChanged;
        Overlay.PreviewKeyDown += OnOverlayKeyDown;
        Overlay.PreviewKeyUp += OnOverlayKeyUp;
        Overlay.LostKeyboardFocus += (_, _) => StopKeyboardMove();

        // The overlay is a separate window that doesn't follow WPF visibility: hide its content ourselves,
        // otherwise labels of hidden cameras would float over the focused one.
        IsVisibleChanged += (_, _) => Overlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;

        // When the tile grows (focus mode, fullscreen) the native host doesn't repaint the newly
        // exposed letterbox area, which showed up as a stray strip. Repaint after each resize.
        // LibVLC resizes its own window a moment later, so repaint again shortly after.
        _repaint = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _repaint.Tick += (_, _) =>
        {
            _repaint.Stop();
            Helpers.VideoHostBackground.Apply(Video);
        };
        Overlay.SizeChanged += (_, _) => UpdateBars();
        Video.SizeChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() => Helpers.VideoHostBackground.Apply(Video), System.Windows.Threading.DispatcherPriority.Background);
            _repaint.Stop();
            _repaint.Start();
        };
    }

    /// <summary>True when this tile lives in a detached window instead of the mosaic.</summary>
    public bool IsInDetachedWindow
    {
        get => (bool)GetValue(IsInDetachedWindowProperty);
        set => SetValue(IsInDetachedWindowProperty, value);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (CameraTile)d;
        var detached = (bool)e.NewValue;
        tile.DetachButton.Icon = new Wpf.Ui.Controls.SymbolIcon(
            detached ? Wpf.Ui.Controls.SymbolRegular.WindowArrowUp24 : Wpf.Ui.Controls.SymbolRegular.WindowNew24);
        tile.DetachButton.ToolTip = detached ? "Voltar ao mosaico" : "Abrir em janela separada";
        tile.FocusButton.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
        tile.UpdateAttachment();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tile is not null)
        {
            _tile.PropertyChanged -= OnTileChanged;
            _tile.Stream.Release(Video);
        }

        _tile = e.NewValue as CameraTileViewModel;
        Overlay.DataContext = _tile; // the overlay lives in a separate window; pass the context explicitly
        if (_tile is not null)
        {
            _tile.PropertyChanged += OnTileChanged;
            ProblemGlyph.Text = char.ConvertFromUtf32((int)_tile.ProblemIcon);
            ShowAudioState();
            ShowPtzState();
        }

        UpdateAttachment();
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraTileViewModel.IsDetached))
        {
            UpdateAttachment();
        }
        else if (e.PropertyName == nameof(CameraTileViewModel.ProblemIcon) && _tile is not null)
        {
            ProblemGlyph.Text = char.ConvertFromUtf32((int)_tile.ProblemIcon);
        }
        else if (e.PropertyName == nameof(CameraTileViewModel.IsAudioOn))
        {
            ShowAudioState();
        }
        else if (e.PropertyName == nameof(CameraTileViewModel.IsPtzMode))
        {
            ShowPtzState();
        }
        else if (e.PropertyName == nameof(CameraTileViewModel.Status))
        {
            if (_tile?.Status == Mirante.App.Video.StreamStatus.Live)
            {
                // LibVLC creates its video window when playback starts; paint around it once it's there.
                _repaint.Stop();
                _repaint.Start();
                ReadVideoSize();
            }

            UpdateBars();
        }
    }

    /// <summary>Video resolution, read off the UI thread (LibVLC calls can block briefly).</summary>
    private void ReadVideoSize()
    {
        var player = _tile?.Stream.Player;
        if (player is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            uint width = 0, height = 0;
            var known = player.Size(0, ref width, ref height) && width > 0 && height > 0;
            Dispatcher.BeginInvoke(() =>
            {
                _videoSize = known ? (width, height) : null;
                UpdateBars();
            });
        });
    }

    /// <summary>Dark frame around the picture: the tile minus the area the video actually fills.</summary>
    private void UpdateBars()
    {
        double w = Overlay.ActualWidth, h = Overlay.ActualHeight;
        if (_tile?.Status != Mirante.App.Video.StreamStatus.Live || _videoSize is not var (vw, vh) || w <= 0 || h <= 0)
        {
            Bars.Data = null;
            return;
        }

        var scale = Math.Min(w / vw, h / vh);
        double pw = vw * scale, ph = vh * scale;
        var picture = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);
        var frame = new System.Windows.Media.GeometryGroup { FillRule = System.Windows.Media.FillRule.EvenOdd };
        frame.Children.Add(new System.Windows.Media.RectangleGeometry(new Rect(0, 0, w, h)));
        frame.Children.Add(new System.Windows.Media.RectangleGeometry(picture));
        frame.Freeze();
        Bars.Data = frame;
    }

    private void ShowAudioState()
    {
        var on = _tile?.IsAudioOn == true;
        AudioButton.Icon = new Wpf.Ui.Controls.SymbolIcon(
            on ? Wpf.Ui.Controls.SymbolRegular.Speaker224 : Wpf.Ui.Controls.SymbolRegular.SpeakerMute24);
        AudioButton.ToolTip = on ? "Desligar o som (M)" : "Ligar o som (M)";
        AudioGlyph.Text = char.ConvertFromUtf32((int)Wpf.Ui.Controls.SymbolRegular.Speaker224);
    }

    private void ShowPtzState()
    {
        var on = _tile?.IsPtzMode == true;
        PtzButton.Appearance = on ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
        PtzButton.ToolTip = on ? "Esconder as setas" : "Mover a câmera (as setas do teclado também movem)";
    }

    private void OnPtzArrowDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } arrow || _tile is null)
        {
            return;
        }

        var parts = tag.Split(',');
        var pan = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var tilt = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        arrow.CaptureMouse();
        arrow.Background = (System.Windows.Media.Brush)FindResource("PtzArrowPressed");
        _tile.Ptz.StartMove(pan * Mirante.App.Video.CameraPtz.Speed, tilt * Mirante.App.Video.CameraPtz.Speed);
        e.Handled = true; // not a double-click on the video
    }

    private void OnPtzArrowUp(object sender, MouseButtonEventArgs e)
    {
        (sender as Border)?.ReleaseMouseCapture(); // LostMouseCapture stops the move
        e.Handled = true;
    }

    private void OnPtzArrowLost(object sender, MouseEventArgs e)
    {
        (sender as Border)?.ClearValue(Border.BackgroundProperty);
        _tile?.Ptz.StopMove();
    }

    private static bool IsArrow(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary>
    /// Arrow keys move the camera while held, only with the arrows showing. One direction at a time:
    /// cameras like the Tapo C210 run a diagonal as one axis and then the other, so the last key
    /// pressed wins, and releasing it goes back to a key still held.
    /// </summary>
    private bool MoveFromKeyboard(Key key, bool pressed)
    {
        if (_tile is not { IsPtzMode: true })
        {
            return false;
        }

        if (pressed)
        {
            _ptzKey = key;
        }
        else if (key == _ptzKey)
        {
            _ptzKey = ((Key[])[Key.Left, Key.Right, Key.Up, Key.Down]).Where(k => k != key && Keyboard.IsKeyDown(k))
                .Cast<Key?>().FirstOrDefault();
        }

        var speed = Mirante.App.Video.CameraPtz.Speed;
        var (pan, tilt) = _ptzKey switch
        {
            Key.Left => (-speed, 0.0),
            Key.Right => (speed, 0.0),
            Key.Up => (0.0, speed),
            Key.Down => (0.0, -speed),
            _ => (0.0, 0.0),
        };
        _tile.Ptz.StartMove(pan, tilt); // (0, 0) stops
        return true;
    }

    private void OnOverlayKeyUp(object sender, KeyEventArgs e)
    {
        if (IsArrow(e.Key))
        {
            e.Handled = MoveFromKeyboard(e.Key, pressed: false);
        }
    }

    /// <summary>Focus went elsewhere while a key was held: the key-up will never come here.</summary>
    private void StopKeyboardMove()
    {
        if (_tile is { IsPtzMode: true } && Mouse.Captured is not Border)
        {
            _ptzKey = null;
            _tile.Ptz.StopMove();
        }
    }

    /// <summary>The mosaic tile shows video unless the camera is detached; the detached window does the opposite.</summary>
    private void UpdateAttachment()
    {
        if (!IsLoaded || _tile is null)
        {
            return;
        }

        if (IsInDetachedWindow == _tile.IsDetached)
        {
            _tile.Stream.Attach(Video);
        }
        else
        {
            _tile.Stream.Release(Video);
        }
    }

    private void OnOverlayMouseEnter(object sender, MouseEventArgs e) => FadeActions(1);

    private void OnOverlayMouseLeave(object sender, MouseEventArgs e) => FadeActions(0);

    private void FadeActions(double to) =>
        Actions.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() });

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _tile is not null && Window.GetWindow(this) is IShortcutHost host)
        {
            host.OnTileDoubleClick(_tile);
            e.Handled = true;
        }
    }

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (Window.GetWindow(this) is IShortcutHost host)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && _tile is not null)
            {
                _tile.SnapshotCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (IsArrow(e.Key) && Keyboard.Modifiers == ModifierKeys.None && MoveFromKeyboard(e.Key, pressed: true))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None && _tile is not null)
            {
                _tile.ToggleAudioCommand.Execute(null);
                e.Handled = true;
                return;
            }

            e.Handled = host.HandleShortcut(e.Key, Keyboard.Modifiers);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.DataContext = _tile;
            menu.PlacementTarget = MoreButton;
            menu.IsOpen = true;
        }
    }
}
