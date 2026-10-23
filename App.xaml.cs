using System;
using System.Threading;
using System.Windows;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

public partial class App : Application
{
    const string InstanceMutexName = "Local\\WhisperNote.SingleInstance";
    const string RestoreEventName = "Local\\WhisperNote.Restore";

    public static AppState? AppState { get; private set; }
    public static HardwareBackend DetectedBackend { get; internal set; } = HardwareBackend.Unknown;
    static LlmServer? _server;
    Mutex? _instanceMutex;
    EventWaitHandle? _restoreEvent;

    public static void RegisterServerForCleanup(LlmServer server) => _server = server;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error($"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
            Shutdown(1);
        };

        try
        {
            if (!AcquireSingleInstanceLock())
            {
                Logger.Info("Another WhisperNote instance is already running; exiting");
                Shutdown();
                return;
            }

            DetectedBackend = HardwareDetector.Detect();
            Logger.Info($"Hardware backend: {DetectedBackend}");
            var settings = AppSettings.Load();
            AppState = new AppState(settings);

            // Seed before the first window binds: the XAML fallback and the
            // converter-owned live brushes only carry the default palette, so a
            // saved pick must be applied before anything paints or captures them.
            PaletteTheme.Apply(VisualizerPaletteCatalog.Resolve(settings.VisualizerPalette));

            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillServer();
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Logger.Error($"Unhandled domain exception: {args.ExceptionObject}");
                KillServer();
            };

            var autostart = IsAutostartLaunch(e.Args);
            if (autostart)
                Logger.Info("Launched from Windows startup");
            var startHidden = autostart || settings.StartInTray;

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            if (!startHidden)
                mainWindow.Show();
        }
        catch (Exception ex)
        {
            Logger.Error($"Fatal startup error: {ex}");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("WPF dispatcher exiting");
        base.OnExit(e);
    }

    static bool IsAutostartLaunch(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/autostart", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    bool AcquireSingleInstanceLock()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalRunningInstance();
            return false;
        }

        StartRestoreListener();
        return true;
    }

    static void SignalRunningInstance()
    {
        try
        {
            using var restore = EventWaitHandle.OpenExisting(RestoreEventName);
            restore.Set();
        }
        catch (Exception ex)
        {
            Logger.Error($"Restore signal failed: {ex.Message}");
        }
    }

    void StartRestoreListener()
    {
        _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreEventName, out _);
        var dispatcher = Dispatcher;
        var listener = new Thread(() =>
        {
            try
            {
                while (_restoreEvent.WaitOne())
                    dispatcher.BeginInvoke(RestoreMainWindow);
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true
        };
        listener.Start();
    }

    void RestoreMainWindow()
    {
        if (MainWindow == null)
            return;

        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        MainWindow.Activate();
    }

    static void KillServer() => _server?.Dispose();
}
