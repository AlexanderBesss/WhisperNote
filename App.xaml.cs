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
        Logger.Initialize();

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
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillServer();
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
