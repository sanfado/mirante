using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mirante.App.Helpers;
using Mirante.App.ViewModels;
using Mirante.Core.Cameras;
using Mirante.Core.Rtsp;
using Mirante.Core.Security;
using Wpf.Ui.Controls;

namespace Mirante.App.Views;

internal sealed partial class CameraEditorWindow : FluentWindow
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private readonly CameraConfig? _existing;
    private readonly Func<Guid, CameraCredential?> _storedCredential;
    private readonly string? _storedUser;
    private bool _confirmSaveUnverified;

    internal CameraEditorWindow(CameraConfig? existing, Func<Guid, CameraCredential?> storedCredential)
    {
        InitializeComponent();
        _existing = existing;
        _storedCredential = storedCredential;

        if (existing is null)
        {
            QualityBox.SelectedIndex = 0;
            Loaded += (_, _) => NameBox.Focus();
            return;
        }

        Title = Heading.Text = "Editar câmera";
        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString(CultureInfo.InvariantCulture);
        PathBox.Text = existing.StreamPath;
        QualityBox.SelectedIndex = existing.StreamPath switch
        {
            CameraConfig.HighQualityStream => 0,
            CameraConfig.LowQualityStream => 1,
            _ => -1,
        };
        _storedUser = storedCredential(existing.Id)?.UserName;
        UserBox.Text = _storedUser ?? string.Empty;
        PasswordBox.PlaceholderText = _storedUser is null ? string.Empty : "Deixe em branco para manter a atual";
    }

    internal CameraEditResult? Result { get; private set; }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityBox.SelectedItem is ComboBoxItem { Tag: string path })
        {
            PathBox.Text = path;
        }
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        _confirmSaveUnverified = false;
        if (SaveButton is not null)
        {
            SaveButton.Content = "Salvar";
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var config, out var credential))
        {
            return;
        }

        var result = await ProbeAsync(config, credential);
        switch (result.Status)
        {
            case RtspProbeStatus.Ok:
                Show(InfoBarSeverity.Success, "Tudo certo!", "A câmera respondeu e aceitou o usuário e a senha.");
                break;
            default:
                ShowProblem(result, config);
                break;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var config, out var credential))
        {
            return;
        }

        if (!_confirmSaveUnverified)
        {
            var result = await ProbeAsync(config, credential);
            if (result.Status == RtspProbeStatus.AuthenticationFailed)
            {
                ShowProblem(result, config);
                return;
            }

            if (result.Status != RtspProbeStatus.Ok)
            {
                ShowProblem(result, config);
                Show(InfoBarSeverity.Warning, "Não consegui confirmar a câmera",
                    $"{ResultBar.Message} Clique em “Salvar mesmo assim” se quiser guardar assim mesmo.");
                _confirmSaveUnverified = true;
                SaveButton.Content = "Salvar mesmo assim";
                return;
            }
        }

        Result = new CameraEditResult(config, credential);
        DialogResult = true;
    }

    /// <summary>Reads and validates the form. <paramref name="credential"/> is null when the stored one is kept.</summary>
    private bool TryReadForm(out CameraConfig config, out CameraCredential? credential)
    {
        credential = null;
        _ = int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port);
        config = (_existing ?? CameraConfig.CreateNew(string.Empty, string.Empty)) with
        {
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            StreamPath = PathBox.Text.Trim(),
        };

        var errors = CameraValidator.Validate(config);
        if (errors.Count > 0)
        {
            Show(InfoBarSeverity.Error, "Confira os dados", ValidationText.For(errors[0]));
            return false;
        }

        var user = UserBox.Text.Trim();
        var password = PasswordBox.Password ?? string.Empty;
        if (user.Length == 0)
        {
            Show(InfoBarSeverity.Error, "Falta o usuário", "Informe o usuário da conta da câmera.");
            return false;
        }

        var keepsStoredPassword = password.Length == 0 && _storedUser is not null && user == _storedUser;
        if (keepsStoredPassword)
        {
            return true;
        }

        if (password.Length == 0)
        {
            Show(InfoBarSeverity.Error, "Falta a senha", "Informe a senha da conta da câmera.");
            return false;
        }

        credential = new CameraCredential(user, password);
        return true;
    }

    private async Task<RtspProbeResult> ProbeAsync(CameraConfig config, CameraCredential? credential)
    {
        SetBusy(true);
        try
        {
            return await RtspProbe.ProbeAsync(RtspAddress.Build(config),
                credential ?? _storedCredential(config.Id), ProbeTimeout);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowProblem(RtspProbeResult result, CameraConfig config)
    {
        switch (result.Status)
        {
            case RtspProbeStatus.AuthenticationFailed:
                Show(InfoBarSeverity.Error, "Usuário ou senha recusados",
                    "A câmera respondeu, mas não aceitou o login. Use a “Conta da câmera”, não a conta da nuvem.");
                break;
            case RtspProbeStatus.Unreachable:
            case RtspProbeStatus.Timeout:
                Show(InfoBarSeverity.Warning, "A câmera não respondeu",
                    $"Nada respondeu em {config.Host}:{config.Port}. Confira o IP e se a câmera está ligada.");
                break;
            default:
                Show(InfoBarSeverity.Warning, "Resposta inesperada",
                    $"A câmera respondeu de um jeito inesperado ({result.Detail}). Confira o caminho do vídeo.");
                break;
        }
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TestButton.IsEnabled = SaveButton.IsEnabled = !busy;
    }
}
