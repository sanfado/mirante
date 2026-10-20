using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>Asks for the name of a saved camera position. Plain WPF: no video here.</summary>
internal sealed partial class PositionNameWindow : FluentWindow
{
    internal PositionNameWindow(string cameraName, string suggestion)
    {
        InitializeComponent();
        Hint.Text = $"Guarda para onde “{cameraName}” está apontando agora. Depois é só escolher o nome em Posições para voltar.";
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
