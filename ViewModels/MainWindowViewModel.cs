    using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    public double WindowOpacity => _isHighlighted || _isFocused || RecordingManager.IsRecording || RecordingManager.IsProcessing ? 1.0 : 0.55;

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

    bool _useCpuOnly;
    public bool UseCpuOnly
    {
        get => _useCpuOnly;
        set
        {
            if (SetProperty(ref _useCpuOnly, value))
            {
                _state.UseCpuOnly = value;
                UpdateHardwareMode();
                RecordingManager.InfoText = value
                    ? "CPU mode enabled (server will restart)"
                    : "CPU mode disabled (server will restart)";
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
                _state.StartupEnabled = value;
                StartupRegistry.SetEnabled(value);
                RecordingManager.InfoText = value ? "Added to startup" : "Removed from startup";
            }
        }
    }

    bool _autoPaste;
    public bool AutoPaste
    {
        get => _autoPaste;
        set
        {
            if (SetProperty(ref _autoPaste, value))
                _state.AutoPaste = value;
        }
    }

    bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
                _state.MinimizeToTray = value;
        }
    }

    bool _startInTray;
    public bool StartInTray
    {
        get => _startInTray;
        set
        {
            if (SetProperty(ref _startInTray, value))
                _state.StartInTray = value;
        }
    }

    public string? LocalModelId => _state.LocalModelId;

    public Brush HardwareModeForeground { get; } = CreateHardwareModeBrush();

    static SolidColorBrush CreateHardwareModeBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(124, 252, 0));
        brush.Freeze();
        return brush;
    }

    string _hardwareMode = "";
    public string HardwareMode
    {
        get => _hardwareMode;
        set => SetProperty(ref _hardwareMode, value);
    }

    void UpdateHardwareMode()
    {
        // Once the server is up, report the backend it actually runs on: the startup
        // fallback chain may have demoted a GPU the preferred build cannot drive.
        var backend = ServerManager.IsServerRunning ? ServerManager.Backend : App.DetectedBackend;
        HardwareMode = _useCpuOnly ? "CPU" : backend switch
        {
            HardwareBackend.Vulkan => "GPU",
            HardwareBackend.IntelNpu => "NPU",
            HardwareBackend.NvidiaCuda or HardwareBackend.NvidiaCudaLegacy => "GPU",
            HardwareBackend.Cpu => "CPU",
            _ => ""
        };
    }

    bool _modelMissing;
    public bool ModelMissing
    {
        get => _modelMissing;
        set => SetProperty(ref _modelMissing, value);
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

    internal static string VkCodeToString(int vk) => vk switch
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

    public ICommand ServerCommand { get; }
    public ICommand DownloadModelCommand { get; }

    public MainWindowViewModel(AppState state)
    {
        _state = state;
        ServerManager = new ServerStateManager(state);
        // The startup fallback chain may settle on another backend than the detected one.
        ServerManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerManager.Status))
                UpdateHardwareMode();
        };
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

        _autoOffloadVram = state.AutoOffloadVram;
        _useCpuOnly = state.UseCpuOnly;
        _startupEnabled = state.StartupEnabled;
        _autoPaste = state.AutoPaste;
        _minimizeToTray = state.MinimizeToTray;
        _startInTray = state.StartInTray;
        if (!_startupEnabled && StartupRegistry.IsEnabled())
        {
            _startupEnabled = true;
            state.StartupEnabled = true;
        }
        _hotkeyEnabled = state.HotkeyEnabled;
        _hotkeyVirtualKeyCode = state.HotkeyVirtualKeyCode;
        _hotkeyName = VkCodeToString(state.HotkeyVirtualKeyCode);
        UpdateHardwareMode();

        CheckModelExists();

        ServerCommand = new RelayCommand(_ => FireAndForget(
            ServerManager.ToggleServerAsync(s => RecordingManager.InfoText = s ?? ""),
            "ToggleServer"));
        DownloadModelCommand = new RelayCommand(_ => FireAndForget(DownloadModelAsync(), "DownloadModel"));

        if (_hotkeyEnabled)
            InstallHook();

        FireAndForget(InitializeAsync(), "Initialize");
    }

    public void ApplySettings(
        bool autoOffloadVram,
        bool useCpuOnly,
        bool startupEnabled,
        bool autoPaste,
        bool minimizeToTray,
        bool startInTray,
        string? localModelId,
        bool hotkeyEnabled,
        int hotkeyVirtualKeyCode)
    {
        var cpuModeChanged = _state.UseCpuOnly != useCpuOnly;
        var localModelChanged = _state.SetLocalModel(localModelId);

        AutoOffloadVram = autoOffloadVram;
        UseCpuOnly = useCpuOnly;
        StartupEnabled = startupEnabled;
        AutoPaste = autoPaste;
        MinimizeToTray = minimizeToTray;
        StartInTray = startInTray;
        HotkeyEnabled = hotkeyEnabled;
        HotkeyVirtualKeyCode = hotkeyVirtualKeyCode;

        if (cpuModeChanged && ServerManager.IsLocal && ServerManager.IsServerRunning)
            FireAndForget(ServerManager.StopServerAsync(), "StopServerForCpuMode");

        // The running server still holds the previous model; stop it so the next
        // transcription restarts with the selected one (downloading it if needed).
        if (localModelChanged && ServerManager.IsLocal && ServerManager.IsServerRunning)
            FireAndForget(ServerManager.StopServerAsync(), "StopServerForModelChange");
    }

    async Task InitializeAsync()
    {
        Logger.Info("Available microphones:");
        AudioRecorder.LogAvailableDevices();
        Logger.Info("App started");
        await ServerManager.InitializeAsync();
    }

    void CheckModelExists()
    {
        var provider = _state.ActiveProvider;
        if (provider == null || !provider.IsLocal || string.IsNullOrEmpty(provider.Model))
        {
            ModelMissing = false;
            return;
        }

        var bundledPath = Path.Combine(AppPaths.BundledModelsDirectory, provider.Model);
        var writablePath = AppPaths.WritableModelPath(provider.Model);
        ModelMissing = !File.Exists(bundledPath) && !File.Exists(writablePath);
    }

    async Task DownloadModelAsync()
    {
        var provider = _state.ActiveProvider;
        if (provider == null || string.IsNullOrEmpty(provider.HfRepo) || string.IsNullOrEmpty(provider.Model))
        {
            RecordingManager.InfoText = "No download source configured";
            return;
        }

        RecordingManager.InfoText = "Downloading model...";

        try
        {
            RecordingManager.InfoText = "Stopping server to download model...";
            await ServerManager.StopServerAsync();
            await Task.Delay(1500);

            var destPath = AppPaths.WritableModelPath(provider.Model);
            await ModelDownloader.EnsureModelAsync(
                provider.HfRepo,
                provider.Model,
                destPath,
                (msg, downloaded, total) =>
                {
                    RecordingManager.InfoText = FormatProgressMessage(msg, downloaded, total);
                });

            if (File.Exists(destPath))
            {
                ModelMissing = false;
                RecordingManager.InfoText = "Model downloaded successfully";
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[DownloadModel] failed: {ex.Message}");
            RecordingManager.InfoText = $"Download failed: {ex.Message}";
        }
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
            FireAndForget(ServerManager.StartAsync((msg, _, _) => RecordingManager.InfoText = msg), "StartServer");

        if (isHotkey && !_hotkeyPressed)
            return;

        await RecordingManager.StartRecording(isHotkey, HotkeyName);

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
                var copied = TrySetClipboardText(text);
                var pasted = false;
                if (copied && _autoPaste)
                    pasted = AutoPaster.SendCtrlV();
                RecordingManager.SetSuccess(text);
                if (!copied)
                    RecordingManager.InfoText = "Transcribed, but clipboard was unavailable";
                else if (pasted)
                    RecordingManager.InfoText = "Copied and pasted";
                NotificationSound.Play();
                await OffloadServerAfterSuccessAsync();
            }
            else
            {
                // The request was processed but the model returned nothing
                // (silence or a failed ASR pass). Show it instead of silently
                // dropping back to Ready, which looks like a lost request.
                _ = RecordingManager.SetError("No speech detected, try again");
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
