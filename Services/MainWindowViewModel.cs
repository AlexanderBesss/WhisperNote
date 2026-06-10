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
                 Logger.Info($"[Hotkey press] CanStart={RecordingManager.CanStart}, state={RecordingManager.State}");
                 if (RecordingManager.CanStart)
                 {
                     _hotkeyPressed = true;
                     _ = StartHoldRecord(true);
                 }
                 await Task.CompletedTask;
             },
             async () =>
             {
                 Logger.Info($"[Hotkey release] _hotkeyPressed={_hotkeyPressed}, IsRecording={RecordingManager.IsRecording}, state={RecordingManager.State}");
                 _hotkeyPressed = false;
                 if (RecordingManager.IsRecording)
                 {
                     try
                     {
                         await StopHoldRecord();
                     }
                     catch (Exception ex)
                     {
                         Logger.Error($"[Hotkey release] exception: {ex.Message}");
                         RecordingManager.Reset();
                     }
                 }
             }
         );
        HotkeyName = VkCodeToString(_hotkeyVirtualKeyCode);
        RecordingManager.InfoText = $"Hotkey: {HotkeyName}";
    }

    async Task HandleRecord()
    {
        var state = RecordingManager.State;
        Logger.Info($"[HandleRecord] clicked, state={state}, isRecording={RecordingManager.IsRecording}, canStart={RecordingManager.CanStart}");
        try
        {
            if (RecordingManager.IsRecording)
            {
                Logger.Info("[HandleRecord] stopping recording...");
                var pcm = await RecordingManager.StopRecording();
                Logger.Info($"[HandleRecord] stopped, pcm.Length={pcm.Length}, state after stop={RecordingManager.State}");
                await ProcessAudio(pcm);
            }
            else if (RecordingManager.CanStart)
            {
                Logger.Info("[HandleRecord] starting recording...");
                await StartHoldRecord(isHotkey: false);
                Logger.Info($"[HandleRecord] after StartHoldRecord, state={RecordingManager.State}");
            }
            else
            {
                Logger.Info($"[HandleRecord] cannot start, state={state}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[HandleRecord] exception: {ex.Message}");
            RecordingManager.Reset();
        }
    }

   public async Task StartHoldRecord(bool isHotkey = true)
    {
        Logger.Info($"[StartHoldRecord] isHotkey={isHotkey}, _hotkeyPressed={_hotkeyPressed}");
        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !ServerManager.IsServerRunning)
        {
            Logger.Info("[StartHoldRecord] server not running, starting...");
            try
            {
                RecordingManager.InfoText = "Starting server...";
                await ServerManager.StartAsync((msg, downloaded, total) =>
                {
                    RecordingManager.InfoText = total > 0
                        ? $"{msg} ({ModelDownloader.FormatBytes(downloaded)}/{ModelDownloader.FormatBytes(total)})"
                        : msg;
                });
                Logger.Info("[StartHoldRecord] server started");
            }
            catch (Exception ex)
            {
                Logger.Error($"[StartHoldRecord] server start: {ex.Message}");
                RecordingManager.InfoText = ex.Message;
                return;
            }
        }

        if (isHotkey && !_hotkeyPressed)
        {
            Logger.Info("[StartHoldRecord] hotkey already released, aborting");
            return;
        }

        Logger.Info("[StartHoldRecord] starting recording...");
        await RecordingManager.StartRecording(isHotkey);
        Logger.Info($"[StartHoldRecord] after StartRecording, state={RecordingManager.State}");

        if (isHotkey && !_hotkeyPressed && RecordingManager.IsRecording)
        {
            Logger.Info("[StartHoldRecord] hotkey released during start, auto-stopping");
            _ = StopHoldRecord();
        }
    }

   public async Task StopHoldRecord()
    {
        Logger.Info($"[StopHoldRecord] entry, state={RecordingManager.State}");
        try
        {
            var pcm = await RecordingManager.StopRecording();
            Logger.Info($"[StopHoldRecord] pcm.Length={pcm.Length}, state after stop={RecordingManager.State}");
            await ProcessAudio(pcm);
        }
        catch (Exception ex)
            {
                Logger.Error($"[StopHoldRecord] exception: {ex.Message}");
                RecordingManager.Reset();
                RecordingManager.InfoText = "Failed to stop recording";
            }
    }

    async Task ProcessAudio(byte[] pcm)
    {
        Logger.Info($"[ProcessAudio] pcm.Length={pcm.Length}, state={RecordingManager.State}");
        if (pcm.Length == 0)
        {
            Logger.Info("[ProcessAudio] empty PCM, cancelling");
            var cancelled = RecordingManager.Cancel();
            Logger.Info($"[ProcessAudio] Cancel() returned {cancelled}, state={RecordingManager.State}");
            return;
        }

        _transcriptionCts?.Cancel();
        _transcriptionCts = new CancellationTokenSource();
        var ct = _transcriptionCts.Token;

        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !await ServerManager.IsServerReady())
        {
            if (!ServerManager.IsServerRunning)
            {
                Logger.Info("[ProcessAudio] server not running, starting...");
                RecordingManager.InfoText = "Starting server...";
                try
                {
                    await ServerManager.StartAsync((msg, _, _) => RecordingManager.InfoText = msg);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[ProcessAudio] server start failed: {ex.Message}");
                    _ = RecordingManager.SetError(ex.Message);
                    return;
                }
            }

            Logger.Info("[ProcessAudio] server not ready, waiting...");
            RecordingManager.InfoText = "Waiting for server...";
            var ready = await ServerManager.WaitForServerReady(s => RecordingManager.InfoText = s);
            if (!ready)
            {
                Logger.Error("[ProcessAudio] server failed to start");
                _ = RecordingManager.SetError("Server failed to start");
                return;
            }
            Logger.Info("[ProcessAudio] server ready");
        }

        RecordingManager.InfoText = "Sending to LLM...";
        Logger.Info("[ProcessAudio] sending to LLM...");

        try
        {
            var text = await ServerManager.TranscribeAsync(pcm, RecordingManager.ChannelCount, ct);
            Logger.Info($"[ProcessAudio] transcription result length={text?.Length ?? 0}");

            if (ct.IsCancellationRequested)
            {
                _ = RecordingManager.Cancel();
                return;
            }

            if (_autoOffloadVram && ServerManager.IsLocal)
            {
                ServerManager.OffloadServer();
                RecordingManager.InfoText = "Model offloaded from VRAM";
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                LastTranscription = text;
                Clipboard.SetText(text);
                var ok = RecordingManager.SetSuccess(text);
                Logger.Info($"[ProcessAudio] SetSuccess returned {ok}, state={RecordingManager.State}");
                NotificationSound.Play();
            }
            else
            {
                Logger.Info("[ProcessAudio] empty result, cancelling");
                _ = RecordingManager.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("[ProcessAudio] OperationCanceledException");
            _ = RecordingManager.Cancel();
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] exception: {ex.Message}");
            _ = RecordingManager.SetError(ex.Message);
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
