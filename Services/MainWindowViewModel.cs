using System;
using System.Collections.ObjectModel;
using System.Threading;
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
    GlobalKeyboardHook? _keyboardHook;
    bool _hotkeyPressed;
    CancellationTokenSource? _transcriptionCts;

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
                RecordingManager.InfoText = value
                    ? "Thinking mode enabled (restart server)"
                    : "Thinking mode disabled (restart server)";
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
                OnProviderChanged();
        }
    }

    bool _hotkeyEnabled;
    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set
        {
            if (SetProperty(ref _hotkeyEnabled, value))
            {
                _state.HotkeyEnabled = value;
                if (value)
                    InstallHook();
                else
                {
       _keyboardHook?.Dispose();
                    _keyboardHook = null;
                    RecordingManager.InfoText = "Hotkey disabled";
                }
            }
        }
    }

    int _hotkeyVirtualKeyCode;
    public int HotkeyVirtualKeyCode
    {
        get => _hotkeyVirtualKeyCode;
        set
        {
            if (SetProperty(ref _hotkeyVirtualKeyCode, value))
            {
                _state.HotkeyVirtualKeyCode = value;
                if (_hotkeyEnabled)
                    InstallHook();
            }
        }
    }

    string _hotkeyName = "";
    public string HotkeyName
    {
        get => _hotkeyName;
        set => SetProperty(ref _hotkeyName, value);
    }

    static string VkCodeToString(int vk) => vk switch
    {
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0x10 => "Ctrl",
        0x11 => "Alt",
        0x5B => "Left Win",
        0x5C => "Right Win",
        _ => $"VK_{vk:X}"
    };

    public ObservableCollection<ProviderConfig> Providers => _state.ProvidersObservable;

    public ICommand ServerCommand { get; }
    public ICommand RecordCommand { get; }
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
        _hotkeyEnabled = state.HotkeyEnabled;
        _hotkeyVirtualKeyCode = state.HotkeyVirtualKeyCode;
        _hotkeyName = VkCodeToString(state.HotkeyVirtualKeyCode);

        ServerCommand = new RelayCommand(_ => ServerManager.ToggleServer(s => RecordingManager.InfoText = s ?? ""));
        RecordCommand = new RelayCommand(_ => _ = HandleRecord());
        CloseCommand = new RelayCommand(_ => Application.Current.Shutdown());

        if (_hotkeyEnabled)
            InstallHook();

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

    void InstallHook()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = new GlobalKeyboardHook(
            _hotkeyVirtualKeyCode,
            async () =>
            {
                if (!RecordingManager.IsRecording && !RecordingManager.IsProcessing)
                {
                    _hotkeyPressed = true;
                    await StartHoldRecord(true);
                }
            },
            async () =>
            {
                if (_hotkeyPressed && RecordingManager.IsRecording)
                {
                    try
                    {
                        await StopHoldRecord();
                    }
                    finally
                    {
                        _hotkeyPressed = false;
                    }
                }
            }
        );
        HotkeyName = VkCodeToString(_hotkeyVirtualKeyCode);
        RecordingManager.InfoText = $"Hotkey: {HotkeyName}";
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
                await StartHoldRecord(isHotkey: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Record: {ex.Message}");
        }
    }

    public async Task StartHoldRecord(bool isHotkey = true)
    {
        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !ServerManager.IsServerRunning)
        {
            try
            {
                ServerManager.Start();
                RecordingManager.InfoText = "Starting server...";
            }
            catch (Exception ex)
            {
                Logger.Error($"Server start: {ex.Message}");
                RecordingManager.InfoText = ex.Message;
                return;
            }
        }

        await RecordingManager.StartRecording(isHotkey);
    }

    public async Task StopHoldRecord()
    {
        try
        {
            var pcm = await RecordingManager.StopRecording();
            await ProcessAudio(pcm);
        }
        catch (Exception ex)
        {
            Logger.Error($"StopHoldRecord: {ex.Message}");
            RecordingManager.SetErrorState("Failed to stop recording");
        }
    }

    async Task ProcessAudio(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            RecordingManager.SetReadyState();
            return;
        }

        _transcriptionCts?.Cancel();
        _transcriptionCts = new CancellationTokenSource();
        var ct = _transcriptionCts.Token;

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
            var text = await ServerManager.TranscribeAsync(pcm, RecordingManager.ChannelCount, ct);

            if (ct.IsCancellationRequested)
                return;

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
                NotificationSound.Play();
            }
            else
            {
                RecordingManager.SetNoTextState();
            }
        }
        catch (OperationCanceledException)
        {
            // Suppressed - transcription was superseded by a new request
        }
        catch (Exception ex)
        {
            Logger.Error($"Transcription: {ex.Message}");
            RecordingManager.SetErrorState(ex.Message);
        }
    }

    public void Dispose()
    {
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
       _keyboardHook?.Dispose();
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
