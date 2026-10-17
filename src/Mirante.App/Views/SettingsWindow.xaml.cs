using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mirante.App.Services;
using Mirante.Core.Storage;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

/// <summary>Settings apply and save immediately, like Windows' own Settings app.</summary>
internal sealed partial class SettingsWindow : FluentWindow
{
    private readonly SettingsRepository _repository;
    private readonly bool _loading = true;

    internal SettingsWindow(SettingsRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        var settings = repository.Load();
        ThemeBox.SelectedIndex = settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.System => 2,
            _ => 0,
        };
        FolderText.Text = CurrentFolder(settings);
        VersionText.Text = $"Mirante {AppInfo.Version}";
        _loading = false;
    }

    private static string CurrentFolder(AppSettings settings) => settings.SnapshotFolder ?? AppPaths.DefaultSnapshotFolder;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<AppTheme>(tag, out var theme))
        {
            return;
        }

        _repository.Save(_repository.Load() with { Theme = theme });
        ThemeService.Apply(theme);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) =>
        MainWindow.OpenFolder(CurrentFolder(_repository.Load()));

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha onde salvar as fotos",
            InitialDirectory = CurrentFolder(_repository.Load()),
        };
        if (dialog.ShowDialog(this) == true)
        {
            _repository.Save(_repository.Load() with { SnapshotFolder = dialog.FolderName });
            FolderText.Text = dialog.FolderName;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
