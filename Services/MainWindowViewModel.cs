using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WhisperNote.Config;
using WhisperNote.ViewModels;

namespace WhisperNote.Services;

public class MainWindowViewModel : ViewModel, IDisposable
{
    readonly AppState _state;
    public ServerStateManager ServerManager { get; }
    public RecordingStateManager RecordingManager { get; }

    string _lastTranscription = "";
    public string LastTranscription
    {
        get => _lastTranscription;
        set => SetProperty(ref _lastTranscription, value);
    }

    bool _autoOffloadVram;
    public bool AutoOffloadVram
    {
        get => _autoOffloadVram;
        set
        {
            if (SetProperty(ref _autoOffloadVram, value))
            {
                _state.AutoOffloadVram = value;
                RecordingManager.InfoText = value ? "Auto-offload enabled" : "Auto-offload disabled";
            }
        }
    }

    bool _thinkingEnabled;
    public bool ThinkingEnabled
    {
        get => _thinkingEnabled;
        set
        {
            if (SetProperty(ref _thinkingEnabled, value))
            {
                _state.ThinkingEnabled = value;
                RecordingManager.InfoText = value ? "Thinking mode enabled (restart server)" : "Thinking mode disabled (restart server)";
            }
        }
    }

    bool _startupEnabled;
    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (SetProperty(ref _startupEnabled, value))
            {
                StartupRegistry.SetEnabled(value);
                RecordingManager.InfoText = value ? "Added to startup" : "Removed from startup";
            }
        }
    }

    int _selectedProviderIndex;
    public int SelectedProviderIndex
    {
        get => _selectedProviderIndex;
        set
        {
            if (SetProperty(ref _selectedProviderIndex, value))
            {
                OnProviderChanged();
            }
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<ProviderConfig> Providers =>
        _state.ProvidersObservable;

    public ICommand ServerCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand ClipboardCommand { get; }
    public ICommand CloseCommand { get; }

    public MainWindowViewModel(AppState state)
    {
        _state = state;
        ServerManager = new ServerStateManager(state);
        RecordingManager = new RecordingStateManager();

        _selectedProviderIndex = state.ActiveProviderIndex;
        _autoOffloadVram = state.AutoOffloadVram;
        _thinkingEnabled = state.ThinkingEnabled;
        _startupEnabled = StartupRegistry.IsEnabled();

        ServerCommand = new RelayCommand(_ => ServerManager.ToggleServer(s => RecordingManager.InfoText = s ?? ""));
        RecordCommand = new RelayCommand(_ => _ = HandleRecord());
        ClipboardCommand = new RelayCommand(_ => CopyClipboard());
        CloseCommand = new RelayCommand(_ => Application.Current.Shutdown());

        _ = InitializeAsync();
    }

    async Task InitializeAsync()
    {
        Logger.Info("Available microphones:");
        AudioRecorder.LogAvailableDevices();
        Logger.Info("App started");
        await ServerManager.InitializeAsync();
    }

    void OnProviderChanged()
    {
        if (SelectedProviderIndex < 0 || SelectedProviderIndex >= Providers.Count) return;
        var provider = Providers[SelectedProviderIndex];
        _state.SetActiveProvider(SelectedProviderIndex);
        _ = ServerManager.SwitchProvider(provider);
        RecordingManager.InfoText = $"Provider: {provider.Name} ({provider.Model})";
    }

    void CopyClipboard()
    {
        if (!string.IsNullOrEmpty(LastTranscription))
        {
            Clipboard.SetText(LastTranscription);
            RecordingManager.InfoText = "Copied to clipboard";
        }
        else
        {
            RecordingManager.InfoText = "No previous transcription";
        }
    }

    async Task HandleRecord()
    {
        try
        {
            if (RecordingManager.IsRecording)
            {
                var pcm = await RecordingManager.StopRecording();
                await ProcessAudio(pcm);
            }
            else
            {
                var provider = _state.ActiveProvider;
                if (provider != null && provider.IsLocal && !ServerManager.IsServerRunning)
                {
                    try
                    {
                        ServerManager.Start();
                        ServerManager.ServerDotColor = Brushes.Orange;
                        ServerManager.ServerStatusMessage = "Launching...";
                        ServerManager.ServerStatusTextColor = Brushes.Orange;
                        RecordingManager.InfoText = "Starting server...";
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Server start: {ex.Message}");
                        RecordingManager.InfoText = ex.Message;
                        return;
                    }
                }

                await RecordingManager.StartRecording();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Record: {ex.Message}");
        }
    }

    async Task ProcessAudio(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            RecordingManager.SetReadyState();
            return;
        }

        RecordingManager.SetProcessingState();

        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !await ServerManager.IsServerReady())
        {
            var ready = await ServerManager.WaitForServerReady(s => RecordingManager.InfoText = s);
            if (!ready)
            {
                RecordingManager.SetErrorState("Server failed to start");
                return;
            }
        }

        RecordingManager.InfoText = "Sending to LLM...";

        try
        {
            var text = await ServerManager.TranscribeAsync(pcm, RecordingManager.ChannelCount);

            if (_autoOffloadVram && ServerManager.IsLocal)
            {
                ServerManager.OffloadServer();
                RecordingManager.InfoText = "Model offloaded from VRAM";
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                LastTranscription = text;
                Clipboard.SetText(text);
                RecordingManager.SetSuccessState(text);
                Logger.Info("Success, copied to clipboard");
            }
            else
            {
                RecordingManager.SetNoTextState();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Transcription: {ex.Message}");
            RecordingManager.SetErrorState(ex.Message);
        }
    }

    public void Dispose()
    {
        ServerManager.Dispose();
        RecordingManager.Dispose();
    }
}

public class RelayCommand : ICommand
{
    readonly Action<object?> _execute;
    readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
