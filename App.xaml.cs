using System;
using System.Windows;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

public partial class App : Application
{
    public static AppState? AppState { get; private set; }
    static LlmServer? _server;

    public static void RegisterServerForCleanup(LlmServer server) => _server = server;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Logger.Initialize();
        var settings = AppSettings.Load();
        AppState = new AppState(settings);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillServer();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillServer();
    }

    static void KillServer() => _server?.Dispose();
}
