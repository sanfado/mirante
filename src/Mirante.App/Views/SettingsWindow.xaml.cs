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
    private readonly Action<bool> _onKeepInTrayChanged;
    private readonly UpdateService _updates;
    private readonly Action _applyUpdate;
    private readonly bool _loading = true;

    internal SettingsWindow(SettingsRepository repository, Action<bool> onKeepInTrayChanged,
        UpdateService updates, Action applyUpdate)
    {
        InitializeComponent();
        _repository = repository;
        _onKeepInTrayChanged = onKeepInTrayChanged;
        _updates = updates;
        _applyUpdate = applyUpdate;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        UpdatesToggle.IsChecked = !repository.Load().DisableUpdateChecks;
        ShowUpdateState(null);
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        TrayToggle.IsChecked = repository.Load().KeepInTray;
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

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (StartupToggle.IsChecked == true)
        {
            StartupRegistration.Enable();
        }
        else
        {
            StartupRegistration.Disable();
        }
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        var keep = TrayToggle.IsChecked == true;
        _repository.Save(_repository.Load() with { KeepInTray = keep });
        _onKeepInTrayChanged(keep);
    }

    private void OnUpdatesToggled(object sender, RoutedEventArgs e) =>
        _repository.Save(_repository.Load() with { DisableUpdateChecks = UpdatesToggle.IsChecked != true });

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdateBusy.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            ShowUpdateState(await _updates.CheckAsync());
        }
        finally
        {
            UpdateBusy.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = _updates.IsSupported;
        }
    }

    private void OnApplyUpdateClick(object sender, RoutedEventArgs e) => _applyUpdate();

    private void ShowUpdateState(UpdateCheckResult? result)
    {
        var ready = _updates.ReadyVersion;
        UpdateTitle.Text = ready is null ? $"Versão {_updates.CurrentVersion}" : $"Versão {ready} pronta para instalar";
        UpdateDetail.Text = (_updates.IsSupported, ready, result) switch
        {
            (false, _, _) => "Atualizações funcionam na versão instalada pelo instalador.",
            (_, not null, _) => "Já baixada. O Mirante reinicia em poucos segundos, com tudo como estava.",
            (_, _, UpdateCheckResult.UpToDate) => "Você está na versão mais recente.",
            (_, _, UpdateCheckResult.Failed) => "Não foi possível verificar agora (sem internet ou serviço indisponível).",
            _ => "Você está usando esta versão.",
        };
        CheckUpdatesButton.IsEnabled = _updates.IsSupported;
        CheckUpdatesButton.Visibility = ready is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.Visibility = ready is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
