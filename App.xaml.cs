using System.Windows;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

public partial class App : Application
{
    public static AppState? AppState { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        AppState = new AppState(settings);
    }
}
