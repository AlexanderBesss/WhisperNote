using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote.ViewModels;

public class MainWindowViewModel : ViewModel, IDisposable
{
    readonly AppState _state;
    public ServerStateManager ServerManager { get; }
    public RecordingStateManager RecordingManager { get; }
    GlobalKeyboardHook? _keyboardHook;
    bool _hotkeyPressed;
    CancellationTokenSource? _transcriptionCts;

    bool _isHighlighted;
    bool _isFocused;
    Timer? _highlightTimer;

    public double WindowOpacity => _isHighlighted || _isFocused || RecordingManager.IsRecording || RecordingManager.IsProcessing ? 1.0 : 0.85;

    void SetHighlighted(bool value)
    {
        _isHighlighted = value;
        OnPropertyChanged(nameof(WindowOpacity));
    }

    public void SetFocused(bool value)
    {
        _isFocused = value;
        OnPropertyChanged(nameof(WindowOpacity));
    }

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
                    DisableHook();
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
        0xA3 => "Right Ctrl",
        0xA5 => "Right Alt",
        0x14 => "Caps Lock",
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
        RecordingManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingStateManager.IsRecording) ||
                e.PropertyName == nameof(RecordingStateManager.IsProcessing))
                OnPropertyChanged(nameof(WindowOpacity));
            if (RecordingManager.State == RecordingState.Success)
            {
                SetHighlighted(true);
                _highlightTimer?.Dispose();
                _highlightTimer = new Timer(_ => SetHighlighted(false), null, 3000, Timeout.Infinite);
            }
        };

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

        FireAndForget(InitializeAsync(), "Initialize");
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
        FireAndForget(ServerManager.SwitchProvider(provider), "SwitchProvider");
        RecordingManager.InfoText = $"Provider: {provider.Name} ({provider.Model})";
    }

    void InstallHook()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = new GlobalKeyboardHook(
            _hotkeyVirtualKeyCode,
            async () =>
            {
                if (RecordingManager.CanStart)
                {
                    _hotkeyPressed = true;
                    FireAndForget(StartHoldRecord(true), "StartHoldRecord");
                }
                await Task.CompletedTask;
            },
            async () =>
            {
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

    void DisableHook()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        RecordingManager.InfoText = "Hotkey disabled";
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
            else if (RecordingManager.CanStart)
            {
                await StartHoldRecord(isHotkey: false);
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
        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !ServerManager.IsServerRunning)
        {
            FireAndForget(ServerManager.StartAsync((msg, _, _) => RecordingManager.InfoText = msg), "StartServer");
        }

        if (isHotkey && !_hotkeyPressed)
            return;

        await RecordingManager.StartRecording(isHotkey);

        if (isHotkey && !_hotkeyPressed && RecordingManager.IsRecording)
            FireAndForget(StopHoldRecord(), "StopHoldRecord");
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
            Logger.Error($"[StopHoldRecord] exception: {ex.Message}");
            RecordingManager.Reset();
            RecordingManager.InfoText = "Failed to stop recording";
        }
    }

    async Task ProcessAudio(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            RecordingManager.Cancel();
            return;
        }

        _transcriptionCts?.Cancel();
        _transcriptionCts = new CancellationTokenSource();
        var ct = _transcriptionCts.Token;

        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !await ServerManager.IsServerReady())
        {
            RecordingManager.InfoText = "Waiting for server...";
            await EnsureServerStartedAsync();
        }

        await TranscribeAndHandleResultAsync(pcm, ct);
    }

    async Task EnsureServerStartedAsync()
    {
        try
        {
            await ServerManager.StartAsync((msg, downloaded, total) =>
            {
                RecordingManager.InfoText = total > 0
                    ? $"{msg} ({ModelDownloader.FormatBytes(downloaded)}/{ModelDownloader.FormatBytes(total)})"
                    : msg;
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] server start failed: {ex.Message}");
            _ = RecordingManager.SetError(ex.Message);
            throw;
        }
    }

    async Task TranscribeAndHandleResultAsync(byte[] pcm, CancellationToken ct)
    {
        RecordingManager.InfoText = "Sending to LLM...";

        try
        {
            var text = await ServerManager.TranscribeAsync(pcm, RecordingManager.ChannelCount, ct);

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
                RecordingManager.SetSuccess(text);
                NotificationSound.Play();
            }
            else
            {
                _ = RecordingManager.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            _ = RecordingManager.Cancel();
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] exception: {ex.Message}");
            _ = RecordingManager.SetError(ex.Message);
        }
    }

    static void FireAndForget(Task task, string context)
    {
        _ = HandleAsync(task, context);
    }

    static async Task HandleAsync(Task task, string context)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"[{context}] Background task failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _highlightTimer?.Dispose();
        _keyboardHook?.Dispose();
        ServerManager.Dispose();
        RecordingManager.Dispose();
    }
}
