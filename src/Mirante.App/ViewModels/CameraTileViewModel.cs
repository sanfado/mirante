using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirante.App.Video;
using Wpf.Ui.Controls;

namespace Mirante.App.ViewModels;

internal enum StatusTone
{
    Neutral,
    Good,
    Busy,
    Bad,
}

/// <summary>One camera as shown in the mosaic or in its own window.</summary>
internal sealed partial class CameraTileViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisibleInGrid))]
    private bool _isDetached;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisibleInGrid))]
    private bool _isHiddenByFocus;

    public CameraTileViewModel(CameraStream stream, MainViewModel owner)
    {
        Stream = stream;
        _owner = owner;
        Stream.PropertyChanged += OnStreamChanged;
    }

    public CameraStream Stream { get; }

    public Guid Id => Stream.Config.Id;

    public string Name => Stream.Config.Name;

    public bool IsVisibleInGrid => !IsDetached && !IsHiddenByFocus;

    public bool IsFocused => ReferenceEquals(_owner.FocusedTile, this);

    public bool IsAudioOn => ReferenceEquals(_owner.AudioTile, this);

    public void RefreshAudio() => OnPropertyChanged(nameof(IsAudioOn));

    public StreamStatus Status => Stream.Status;

    public string StatusLabel => Status switch
    {
        StreamStatus.Live => "AO VIVO",
        StreamStatus.Connecting => "CONECTANDO",
        StreamStatus.Reconnecting => "RECONECTANDO",
        StreamStatus.Offline => "SEM CONEXÃO",
        StreamStatus.AuthFailed => "SENHA RECUSADA",
        StreamStatus.MissingCredentials => "SEM SENHA",
        _ => "PAUSADA",
    };

    public StatusTone Tone => Status switch
    {
        StreamStatus.Live => StatusTone.Good,
        StreamStatus.Connecting or StreamStatus.Reconnecting => StatusTone.Busy,
        StreamStatus.Offline or StreamStatus.AuthFailed or StreamStatus.MissingCredentials => StatusTone.Bad,
        _ => StatusTone.Neutral,
    };

    public bool HasNoPicture => Status != StreamStatus.Live;

    public bool ShowSpinner => Status is StreamStatus.Connecting or StreamStatus.Reconnecting;

    public bool ShowProblem => Status is StreamStatus.Offline or StreamStatus.AuthFailed or StreamStatus.MissingCredentials;

    public bool ShowEditAction => Status is StreamStatus.AuthFailed or StreamStatus.MissingCredentials;

    public string BusyText => Status == StreamStatus.Reconnecting ? "Reconectando…" : "Conectando…";

    public SymbolRegular ProblemIcon => Status switch
    {
        StreamStatus.AuthFailed => SymbolRegular.LockClosed24,
        StreamStatus.MissingCredentials => SymbolRegular.PersonKey24,
        _ => SymbolRegular.PlugDisconnected24,
    };

    public string ProblemTitle => Status switch
    {
        StreamStatus.AuthFailed => "A câmera recusou o usuário ou a senha",
        StreamStatus.MissingCredentials => "Falta a senha desta câmera",
        _ => "Sem conexão com a câmera",
    };

    public string ProblemMessage => Status switch
    {
        StreamStatus.AuthFailed => "Confira a “Conta da câmera” no app do fabricante e atualize aqui.",
        StreamStatus.MissingCredentials => "Informe o usuário e a senha da conta da câmera.",
        _ => $"Tentando novamente em {Stream.Config.Host}…",
    };

    public void RefreshConfig()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ProblemMessage));
    }

    public void RefreshFocus() => OnPropertyChanged(nameof(IsFocused));

    [RelayCommand]
    private void Snapshot() => _owner.TakeSnapshot(this);

    [RelayCommand]
    private void ToggleDetach() => _owner.ToggleDetach(this);

    [RelayCommand]
    private void ToggleFocus() => _owner.ToggleFocus(this);

    [RelayCommand]
    private void Fullscreen() => _owner.ShowFullscreen(this);

    [RelayCommand]
    private Task EditAsync() => _owner.EditCameraAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveCameraAsync(this);

    [RelayCommand]
    private void Retry() => Stream.Retry();

    [RelayCommand]
    private void ToggleAudio() => _owner.ToggleAudio(this);

    [RelayCommand]
    private void Organize() => _owner.OrganizeCommand.Execute(null);

    private void OnStreamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CameraStream.Status))
        {
            return;
        }

        foreach (var name in (string[])[nameof(Status), nameof(HasNoPicture), nameof(StatusLabel), nameof(Tone), nameof(ShowSpinner),
                     nameof(ShowProblem), nameof(ShowEditAction), nameof(BusyText), nameof(ProblemIcon),
                     nameof(ProblemTitle), nameof(ProblemMessage)])
        {
            OnPropertyChanged(name);
        }
    }
}
