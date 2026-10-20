using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mirante.App.Services;
using Mirante.Core.Recording;
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
        RecordingFolderText.Text = RecordingService.Resolve(settings);
        Fill(RetentionBox, [1, 3, 7, 14, 30, 90], RecordingFiles.ClampRetentionDays(settings.RecordingRetentionDays),
            d => d == 1 ? "1 dia" : $"{d} dias");
        Fill(SpaceBox, [25, 50, 100, 250, 500, 1000, 2000], RecordingFiles.ClampMaxGigabytes(settings.RecordingMaxGigabytes),
            g => g >= 1000 ? $"{g / 1000.0:0.#} TB" : $"{g} GB");
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

    /// <summary>The usual choices, plus the current value if it was set to something else.</summary>
    private static void Fill(ComboBox box, int[] choices, int current, Func<int, string> label)
    {
        foreach (var value in choices.Append(current).Distinct().Order())
        {
            box.Items.Add(new ComboBoxItem { Content = label(value), Tag = value });
        }

        box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == current);
    }

    private void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RetentionBox.SelectedItem is ComboBoxItem { Tag: int days })
        {
            _repository.Save(_repository.Load() with { RecordingRetentionDays = days });
        }
    }

    private void OnSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SpaceBox.SelectedItem is ComboBoxItem { Tag: int gigabytes })
        {
            _repository.Save(_repository.Load() with { RecordingMaxGigabytes = gigabytes });
        }
    }

    private void OnOpenRecordingFolderClick(object sender, RoutedEventArgs e) =>
        MainWindow.OpenFolder(RecordingService.Resolve(_repository.Load()));

    private void OnChangeRecordingFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha onde salvar as gravações",
            InitialDirectory = RecordingService.Resolve(_repository.Load()),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!RecordingFiles.IsUsableFolder(dialog.FolderName))
        {
            _ = new Wpf.Ui.Controls.MessageBox
            {
                Owner = this,
                Title = "Pasta não pode ser usada",
                Content = "O caminho dessa pasta tem aspas ou chaves ({ }), que o motor de vídeo não aceita. Escolha outra pasta.",
                CloseButtonText = "OK",
            }.ShowDialogAsync();
            return;
        }

        _repository.Save(_repository.Load() with { RecordingFolder = dialog.FolderName });
        RecordingFolderText.Text = dialog.FolderName;
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
