using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>Reorders the cameras (drag and drop or arrows). Plain WPF list: no video here.</summary>
internal sealed partial class OrganizeCamerasWindow : FluentWindow
{
    private readonly ObservableCollection<Item> _items;
    private Point? _dragStart;

    internal OrganizeCamerasWindow(IEnumerable<(Guid Id, string Name)> cameras)
    {
        InitializeComponent();
        _items = new ObservableCollection<Item>(cameras.Select(c => new Item(c.Id, c.Name)));
        List.ItemsSource = _items;
        Renumber();
        List.SelectedIndex = _items.Count > 0 ? 0 : -1;
    }

    /// <summary>The new order (camera ids), or null when cancelled.</summary>
    internal IReadOnlyList<Guid>? Result { get; private set; }

    private void Move(int from, int to)
    {
        if (from < 0 || to < 0 || from >= _items.Count || to >= _items.Count || from == to)
        {
            return;
        }

        _items.Move(from, to);
        Renumber();
        List.SelectedIndex = to;
        List.ScrollIntoView(_items[to]);
    }

    private void Renumber()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Position = i + 1;
        }

        UpdateButtons();
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => Move(List.SelectedIndex, List.SelectedIndex - 1);

    private void OnDownClick(object sender, RoutedEventArgs e) => Move(List.SelectedIndex, List.SelectedIndex + 1);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        UpButton.IsEnabled = List.SelectedIndex > 0;
        DownButton.IsEnabled = List.SelectedIndex >= 0 && List.SelectedIndex < _items.Count - 1;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = [.. _items.Select(i => i.Id)];
        DialogResult = true;
    }

    // ----- Drag and drop -----

    private void OnListMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(List);

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(List) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        if (ItemAt(e.OriginalSource) is { } item)
        {
            DragDrop.DoDragDrop(List, item, DragDropEffects.Move);
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Item)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Item)) is not Item dragged)
        {
            return;
        }

        var target = ItemAt(e.OriginalSource);
        Move(_items.IndexOf(dragged), target is null ? _items.Count - 1 : _items.IndexOf(target));
    }

    private static Item? ItemAt(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem { DataContext: Item item })
            {
                return item;
            }
        }

        return null;
    }

    internal sealed partial class Item(Guid id, string name) : ObservableObject
    {
        [ObservableProperty]
        private int _position;

        public Guid Id { get; } = id;

        public string Name { get; } = name;

        public string Glyph { get; } = char.ConvertFromUtf32((int)SymbolRegular.ReOrderDotsVertical24);
    }
}
