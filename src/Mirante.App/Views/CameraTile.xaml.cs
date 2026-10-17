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

    private CameraTileViewModel? _tile;

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

        // The overlay is a separate window that doesn't follow WPF visibility: hide its content ourselves,
        // otherwise labels of hidden cameras would float over the focused one.
        IsVisibleChanged += (_, _) => Overlay.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;
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
