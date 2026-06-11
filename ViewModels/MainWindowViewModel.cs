using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
    bool _pendingHotkeyStop;
    readonly SemaphoreSlim _recordOperationLock = new(1, 1);
    CancellationTokenSource? _transcriptionCts;

    bool _isHighlighted;
    bool _isFocused;
    readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(3) };

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
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            SetHighlighted(false);
        };
        RecordingManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingStateManager.IsRecording) ||
                e.PropertyName == nameof(RecordingStateManager.IsProcessing))
                OnPropertyChanged(nameof(WindowOpacity));
            if (RecordingManager.State == RecordingState.Success)
            {
                SetHighlighted(true);
                _highlightTimer.Stop();
                _highlightTimer.Start();
            }
        };

        _selectedProviderIndex = state.ActiveProviderIndex;
        _autoOffloadVram = state.AutoOffloadVram;
        _thinkingEnabled = state.ThinkingEnabled;
        _startupEnabled = StartupRegistry.IsEnabled();
        _hotkeyEnabled = state.HotkeyEnabled;
        _hotkeyVirtualKeyCode = state.HotkeyVirtualKeyCode;
        _hotkeyName = VkCodeToString(state.HotkeyVirtualKeyCode);

        ServerCommand = new RelayCommand(_ => FireAndForget(
            ServerManager.ToggleServerAsync(s => RecordingManager.InfoText = s ?? ""),
            "ToggleServer"));
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
        if (RecordingManager.IsRecording || RecordingManager.IsProcessing)
        {
            RecordingManager.InfoText = "Finish the current recording before switching providers";
            _selectedProviderIndex = _state.ActiveProviderIndex;
            OnPropertyChanged(nameof(SelectedProviderIndex));
            return;
        }

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
                    _pendingHotkeyStop = false;
                    FireAndForget(StartHoldRecord(true), "StartHoldRecord");
                }
                await Task.CompletedTask;
            },
            async () =>
            {
                _hotkeyPressed = false;
                if (RecordingManager.IsRecording)
                {
                    await StopHoldRecord(queueIfBusy: true);
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
        await RunRecordingOperation(HandleRecordCore, "HandleRecord");
    }

    async Task HandleRecordCore()
    {
        if (RecordingManager.IsRecording)
        {
            await StopAndProcessCore();
        }
        else if (RecordingManager.CanStart)
        {
            await StartHoldRecordCore(isHotkey: false);
        }
    }

    public async Task StartHoldRecord(bool isHotkey = true)
    {
        await RunRecordingOperation(
            () => StartHoldRecordCore(isHotkey),
            "StartHoldRecord");
    }

    async Task StartHoldRecordCore(bool isHotkey)
    {
        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !ServerManager.IsServerRunning)
        {
            FireAndForget(ServerManager.StartAsync((msg, _, _) => RecordingManager.InfoText = msg), "StartServer");
        }

        if (isHotkey && !_hotkeyPressed)
            return;

        await RecordingManager.StartRecording(isHotkey);

        if (isHotkey && (!_hotkeyPressed || _pendingHotkeyStop) && RecordingManager.IsRecording)
        {
            _pendingHotkeyStop = false;
            await StopAndProcessCore();
        }
    }

    public async Task StopHoldRecord(bool queueIfBusy = false)
    {
        await RunRecordingOperation(
            StopAndProcessCore,
            "StopHoldRecord",
            queueHotkeyStop: queueIfBusy,
            failureInfo: "Failed to stop recording");
    }

    async Task StopAndProcessCore()
    {
        var pcm = await RecordingManager.StopRecording();
        await ProcessAudio(pcm);
    }

    async Task RunRecordingOperation(
        Func<Task> operation,
        string context,
        bool queueHotkeyStop = false,
        string? failureInfo = null)
    {
        if (!await _recordOperationLock.WaitAsync(0))
        {
            if (queueHotkeyStop)
                _pendingHotkeyStop = true;
            return;
        }

        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            _ = RecordingManager.Cancel();
        }
        catch (Exception ex)
        {
            Logger.Error($"[{context}] exception: {ex.Message}");
            RecordingManager.Reset();
            if (!string.IsNullOrEmpty(failureInfo))
                RecordingManager.InfoText = failureInfo;
        }
        finally
        {
            _recordOperationLock.Release();
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
        _transcriptionCts?.Dispose();
        _transcriptionCts = new CancellationTokenSource();
        var ct = _transcriptionCts.Token;

        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !await ServerManager.IsServerReady())
        {
            if (ServerManager.IsStarting)
            {
                RecordingManager.InfoText = "Waiting for server...";
                if (!await ServerManager.WaitForServerReady(s => RecordingManager.InfoText = s))
                {
                    _ = RecordingManager.SetError("Server failed to start");
                    return;
                }
            }
            else
            {
                RecordingManager.InfoText = "Waiting for server...";
                if (!await EnsureServerStartedAsync())
                    return;
            }
        }

        await TranscribeAndHandleResultAsync(pcm, ct);
    }

    async Task<bool> EnsureServerStartedAsync()
    {
        try
        {
            await ServerManager.StartAsync((msg, downloaded, total) =>
            {
                RecordingManager.InfoText = FormatProgressMessage(msg, downloaded, total);
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] server start failed: {ex.Message}");
            _ = RecordingManager.SetError(ex.Message);
            return false;
        }
    }

    static string FormatProgressMessage(string message, long downloaded, long total) =>
        total > 0
            ? $"{message} ({ModelDownloader.FormatBytes(downloaded)}/{ModelDownloader.FormatBytes(total)})"
            : message;

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

            if (!string.IsNullOrWhiteSpace(text))
            {
                LastTranscription = text;
                var copied = TrySetClipboardText(text);
                RecordingManager.SetSuccess(text);
                if (!copied)
                    RecordingManager.InfoText = "Transcribed, but clipboard was unavailable";
                NotificationSound.Play();
                await OffloadServerAfterSuccessAsync();
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

    async Task OffloadServerAfterSuccessAsync()
    {
        if (!_autoOffloadVram || !ServerManager.IsLocal)
            return;

        try
        {
            await ServerManager.OffloadServerAsync();
            RecordingManager.InfoText = "Model offloaded from VRAM";
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] offload failed: {ex.Message}");
        }
    }

    static bool TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Clipboard.SetText failed: {ex.Message}");
            return false;
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
        _highlightTimer.Stop();
        _recordOperationLock.Dispose();
        _keyboardHook?.Dispose();
        ServerManager.Dispose();
        RecordingManager.Dispose();
    }
}
