using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>Asks for a name (saved position, layout). Plain WPF: no video here.</summary>
internal sealed partial class NameWindow : FluentWindow
{
    internal NameWindow(string heading, string hint, string placeholder, string suggestion, string saveText = "Salvar")
    {
        InitializeComponent();
        Title = heading;
        Heading.Text = heading;
        Hint.Text = hint;
        NameBox.PlaceholderText = placeholder;
        SaveButton.Content = saveText;
        NameBox.Text = suggestion;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The name typed, or null when cancelled.</summary>
    internal string? Result { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text.Trim();
        DialogResult = true;
    }
}
