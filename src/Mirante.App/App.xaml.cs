using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Mirante.App.Services;
using Mirante.App.Video;
using Mirante.App.ViewModels;
using Mirante.App.Views;
using Mirante.Core.Platform.Windows;
using Mirante.Core.Storage;

namespace Mirante.App;

[SuppressMessage("Design", "CA1001", Justification = "WPF owns the Application lifetime; resources are released in OnExit.")]
public partial class App : Application
{
    private SingleInstance? _instance;
    private VideoEngine? _engine;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance();
        var isDemo = false;
#if DEBUG
        isDemo = DevTools.DemoMode.TryParse(e.Args, out _, out _); // demo runs alongside a real instance
#endif
        if (!_instance.IsFirst && !isDemo)
        {
            _instance.ActivateFirstInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLog.Write(ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };

        var settingsRepository = new SettingsRepository(AppPaths.SettingsFile);
        var settings = settingsRepository.Load();
        ThemeService.Apply(settings.Theme);

        try
        {
            _engine = new VideoEngine();
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or VLCException)
        {
            CrashLog.Write(ex);
            System.Windows.MessageBox.Show("Não foi possível iniciar o motor de vídeo. Reinstale o Mirante.",
                "Mirante", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var cameras = new CameraRepository(AppPaths.CamerasFile);
        Mirante.Core.Security.ICredentialStore credentials = new WindowsCredentialStore();
#if DEBUG
        var demo = DevTools.DemoMode.TryParse(e.Args, out var demoVideos, out var demoOutput);
        if (demo)
        {
            (cameras, settingsRepository, credentials) = DevTools.DemoMode.Setup(demoVideos, _engine);
        }
#endif
        var snapshotSettings = settingsRepository;
        _viewModel = new MainViewModel(cameras, credentials, _engine,
            () => snapshotSettings.Load().SnapshotFolder ?? AppPaths.DefaultSnapshotFolder, Dispatcher);

        var window = new MainWindow(_viewModel, settingsRepository);
        MainWindow = window;
        window.Show();
#if DEBUG
        if (demo)
        {
            _ = DevTools.DemoMode.RunAsync(window, _viewModel, settingsRepository, demoOutput);
        }
#endif
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(window.BringToFront));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // LibVLC can take a while to stop RTSP streams; don't hang the exit on it.
        _viewModel?.ShutdownAsync().Wait(TimeSpan.FromSeconds(3));
        _engine?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = CrashLog.Write(e.Exception);
        System.Windows.MessageBox.Show(
            $"Algo deu errado e o Mirante precisa fechar.\n\nDetalhes salvos em:\n{path ?? AppPaths.LogFolder}",
            "Mirante", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
