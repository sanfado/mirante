using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>What the user chose when saving a layout.</summary>
internal sealed record SaveLayoutChoice(string Name, IReadOnlyList<Guid> Excluded, bool IncludePositions);

/// <summary>Names a layout and picks its cameras. Plain WPF: no video here.</summary>
internal sealed partial class SaveLayoutWindow : FluentWindow
{
    private readonly List<Item> _items;

    internal SaveLayoutWindow(IEnumerable<(Guid Id, string Name, bool Included)> cameras, string suggestion, bool anyPtz)
    {
        InitializeComponent();
        _items = [.. cameras.Select(c => new Item(c.Id, c.Name) { Included = c.Included })];
        CameraList.ItemsSource = _items;
        NameBox.Text = suggestion;
        PositionsCheck.Visibility = PositionsHint.Visibility = anyPtz ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    internal SaveLayoutChoice? Result { get; private set; }

    /// <summary>A name and at least one camera are needed.</summary>
    private void OnChanged(object sender, RoutedEventArgs e) =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text) && _items.Any(i => i.Included);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = new SaveLayoutChoice(NameBox.Text.Trim(), [.. _items.Where(i => !i.Included).Select(i => i.Id)],
            PositionsCheck.IsChecked == true);
        DialogResult = true;
    }

    private sealed partial class Item(Guid id, string name) : ObservableObject
    {
        public Guid Id { get; } = id;

        public string Name { get; } = name;

        [ObservableProperty]
        private bool _included;
    }
}
