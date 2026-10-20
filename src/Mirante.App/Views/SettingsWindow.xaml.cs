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
        Fill(RetentionBox, [1, 3, 7, 14, 30, 90], RecordingFiles.ClampRetentionDays(settings.RecordingRetentionDays),
            d => d == 1 ? "1 dia" : $"{d} dias");
        _ = ShowLocationsAsync();
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

    // ----- Recording locations -----

    private IReadOnlyList<RecordingLocation> Locations =>
        RecordingStorage.Normalize(_repository.Load().RecordingLocations, AppPaths.DefaultRecordingFolder);

    private void SaveLocations(IEnumerable<RecordingLocation> locations)
    {
        _repository.Save(_repository.Load() with { RecordingLocations = [.. locations] });
        _ = ShowLocationsAsync();
    }

    /// <summary>One card per location; usage and free space are measured in the background.</summary>
    private async Task ShowLocationsAsync()
    {
        var locations = Locations;
        LocationList.Children.Clear();
        var details = new Dictionary<Guid, System.Windows.Controls.TextBlock>();
        for (var i = 0; i < locations.Count; i++)
        {
            var location = locations[i];
            var detail = new System.Windows.Controls.TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = "Medindo…",
            };
            detail.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            details[location.Id] = detail;

            var limit = new ComboBox { MinWidth = 110, ToolTip = "Espaço máximo das gravações neste local" };
            Fill(limit, [25, 50, 100, 250, 500, 1000, 2000, 4000], location.Gigabytes, g => g >= 1000 ? $"{g / 1000.0:0.#} TB" : $"{g} GB");
            limit.SelectionChanged += (_, _) =>
            {
                if (limit.SelectedItem is ComboBoxItem { Tag: int gigabytes })
                {
                    SaveLocations(Locations.Select(l => l.Id == location.Id ? l with { MaxGigabytes = gigabytes } : l));
                }
            };

            var open = new Wpf.Ui.Controls.Button { Margin = new Thickness(8, 0, 0, 0), Appearance = ControlAppearance.Secondary, Content = "Abrir" };
            open.Click += (_, _) => MainWindow.OpenFolder(location.Folder);
            var remove = new Wpf.Ui.Controls.Button
            {
                Margin = new Thickness(8, 0, 0, 0),
                Appearance = ControlAppearance.Secondary,
                Icon = new SymbolIcon(SymbolRegular.Delete24),
                ToolTip = "Deixar de usar este local (as gravações que já estão lá continuam no disco)",
                IsEnabled = locations.Count > 1,
            };
            remove.Click += (_, _) => SaveLocations(Locations.Where(l => l.Id != location.Id));

            var header = new StackPanel();
            header.Children.Add(new System.Windows.Controls.TextBlock
            {
                FontSize = 13.5,
                Text = $"{i + 1}. {location.Folder}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
                ToolTip = location.Folder,
            });
            header.Children.Add(detail);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(limit);
            actions.Children.Add(open);
            actions.Children.Add(remove);
            LocationList.Children.Add(new CardControl
            {
                Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0),
                Icon = new SymbolIcon(SymbolRegular.HardDrive20),
                Header = header,
                Content = actions,
            });
        }

        AddLocationButton.IsEnabled = locations.Count < RecordingStorage.MaxLocations;
        foreach (var state in await RecordingService.MeasureAsync(locations))
        {
            if (details.TryGetValue(state.Location.Id, out var detail))
            {
                detail.Text = !state.Available ? "Não acessível agora (disco desconectado?)"
                    : $"Usando {Size(state.UsedBytes)} de {state.Location.Gigabytes} GB · {Size(state.FreeBytes)} livres no disco"
                      + (state.CanWrite ? string.Empty : " · menos de 5 GB livres: não grava aqui");
            }
        }
    }

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{bytes / (1024.0 * 1024):0} MB";

    private void OnAddLocationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Escolha um local para as gravações (por exemplo, outro HD)" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        var locations = Locations;
        string? problem = !RecordingFiles.IsUsableFolder(folder)
            ? "O caminho dessa pasta tem aspas ou chaves ({ }), que o motor de vídeo não aceita."
            : locations.Any(l => Overlaps(l.Folder, folder))
                ? "Essa pasta já é um local de gravação, ou fica dentro de um (ou contém um). Escolha uma pasta separada."
                : null;
        if (problem is not null)
        {
            _ = new Wpf.Ui.Controls.MessageBox { Owner = this, Title = "Local não pode ser usado", Content = problem, CloseButtonText = "OK" }
                .ShowDialogAsync();
            return;
        }

        SaveLocations([.. locations, new RecordingLocation { Id = Guid.NewGuid(), Folder = folder }]);
    }

    /// <summary>Same folder, or one inside the other (recordings would be counted twice).</summary>
    private static bool Overlaps(string a, string b)
    {
        static string Dir(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var (x, y) = (Dir(a), Dir(b));
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
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
