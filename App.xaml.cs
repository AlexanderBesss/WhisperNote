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
            // Two instances would mean two tray icons, two global hotkey hooks and
            // duplicate recordings/pastes fighting over the same llama server, so a
            // second launch just asks the running one to show its window and exits.
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

            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillServer();
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Logger.Error($"Unhandled domain exception: {args.ExceptionObject}");
                KillServer();
            };

            // The main window is created here, after AppState exists. It used to be
            // created from StartupUri, which races OnStartup under load (e.g. at
            // Windows sign-in): the window could be constructed while AppState was
            // still null, crashing the autostarted instance with a
            // NullReferenceException in ServerStateManager.
            var autostart = IsAutostartLaunch(e.Args);
            if (autostart)
                Logger.Info("Launched from Windows startup");
            var startHidden = autostart || settings.StartInTray;

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            if (!startHidden)
                mainWindow.Show();
            // Hidden start: the window is never shown; the app keeps running in
            // the tray (ShutdownMode is OnExplicitShutdown, so a hidden window
            // does not end the process).
        }
        catch (Exception ex)
        {
            Logger.Error($"Fatal startup error: {ex}");
            Shutdown(1);
        }
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
                // App is shutting down.
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
