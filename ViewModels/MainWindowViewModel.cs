using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote.ViewModels;

public class MainWindowViewModel : ViewModel, IDisposable
{
    readonly AppState _state;
    public ServerStateManager ServerManager { get; }
    public RecordingStateManager RecordingManager { get; private set; }
    GlobalKeyboardHook? _keyboardHook;
    bool _hotkeyPressed;
    bool _pendingHotkeyStop;

    // True from a hotkey press until that press's capture is actually running.
    // Only a release inside that window may be remembered as a pending stop, so
    // a second quick press cannot stop the clip that is already being captured.
    bool _captureStarting;
    readonly SemaphoreSlim _captureLock = new(1, 1);
    readonly TranscriptionQueue _queue;

    // A press that lands while the previous capture is still stopping/resampling
    // waits for the mic instead of being dropped.
    static readonly TimeSpan CaptureWaitTimeout = TimeSpan.FromSeconds(5);

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

    // propertyName is forwarded, not defaulted by SetProperty: the caller here is
    // this helper, so CallerMemberName alone would report "SyncSetting" and every
    // listener keyed on the property name (the overlay, the selection toolbar)
    // would never hear the change. The attribute needs a default value, so the empty
    // string left by an omitted argument is caught by the assert instead.
    bool SyncSetting(ref bool field, bool value, Action persist, string? infoText = null, [CallerMemberName] string propertyName = "")
    {
        System.Diagnostics.Debug.Assert(propertyName.Length > 0, nameof(SyncSetting) + " needs the calling property name");
        if (!SetProperty(ref field, value, propertyName))
            return false;
        persist();
        if (infoText != null)
            RecordingManager.InfoText = infoText;
        return true;
    }

    bool SyncTextSetting(ref string field, string value, Action persist, [CallerMemberName] string propertyName = "")
    {
        System.Diagnostics.Debug.Assert(propertyName.Length > 0, nameof(SyncTextSetting) + " needs the calling property name");
        if (!SetProperty(ref field, value, propertyName))
            return false;
        persist();
        return true;
    }

    bool SyncIntSetting(ref int field, int value, Action persist, [CallerMemberName] string propertyName = "")
    {
        System.Diagnostics.Debug.Assert(propertyName.Length > 0, nameof(SyncIntSetting) + " needs the calling property name");
        if (!SetProperty(ref field, value, propertyName))
            return false;
        persist();
        return true;
    }

    bool _autoOffloadVram;
    public bool AutoOffloadVram
    {
        get => _autoOffloadVram;
        set => SyncSetting(ref _autoOffloadVram, value,
            () => _state.AutoOffloadVram = value,
            value ? "Auto-offload enabled" : "Auto-offload disabled");
    }

    bool _useCpuOnly;
    public bool UseCpuOnly
    {
        get => _useCpuOnly;
        set
        {
            if (SyncSetting(ref _useCpuOnly, value,
                    () => _state.UseCpuOnly = value,
                    value ? "CPU mode enabled (server will restart)" : "CPU mode disabled (server will restart)"))
                UpdateHardwareMode();
        }
    }

    bool _startupEnabled;
    public bool StartupEnabled
    {
        get => _startupEnabled;
        set => SyncSetting(ref _startupEnabled, value, () =>
        {
            _state.StartupEnabled = value;
            StartupRegistry.SetEnabled(value);
        }, value ? "Added to startup" : "Removed from startup");
    }

    bool _autoPaste;
    public bool AutoPaste
    {
        get => _autoPaste;
        set => SyncSetting(ref _autoPaste, value, () => _state.AutoPaste = value);
    }

    bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SyncSetting(ref _minimizeToTray, value, () => _state.MinimizeToTray = value);
    }

    bool _startInTray;
    public bool StartInTray
    {
        get => _startInTray;
        set => SyncSetting(ref _startInTray, value, () => _state.StartInTray = value);
    }

    bool _selectionToolbarEnabled;
    public bool SelectionToolbarEnabled
    {
        get => _selectionToolbarEnabled;
        set => SyncSetting(ref _selectionToolbarEnabled, value,
            () => _state.SelectionToolbarEnabled = value,
            value ? "Selection toolbar enabled" : "Selection toolbar disabled");
    }

    bool _visualizerEnabled = true;
    public bool VisualizerEnabled
    {
        get => _visualizerEnabled;
        set
        {
            if (SyncSetting(ref _visualizerEnabled, value, () => _state.VisualizerEnabled = value,
                    value ? "Voice visualizer enabled" : "Voice visualizer disabled"))
                RecordingManager.SetVisualizerActive(value);
        }
    }

    string _visualizerStyle = VisualizerCatalog.DefaultStyleName;
    public string VisualizerStyle
    {
        get => _visualizerStyle;
        set => SyncTextSetting(ref _visualizerStyle, value, () => _state.VisualizerStyle = value);
    }

    string _visualizerPalette = VisualizerPaletteCatalog.DefaultPaletteName;
    public string VisualizerPalette
    {
        get => _visualizerPalette;
        set => SyncTextSetting(ref _visualizerPalette, value, () => _state.VisualizerPalette = value);
    }

    int _visualizerFps = VisualizerFpsCatalog.DefaultFps;
    public int VisualizerFps
    {
        get => _visualizerFps;
        set => SyncIntSetting(ref _visualizerFps, value, () => _state.VisualizerFps = value);
    }

    string _processingAnimation = ProcessingCatalog.DefaultStyleName;
    public string ProcessingAnimation
    {
        get => _processingAnimation;
        set => SyncTextSetting(ref _processingAnimation, value, () => _state.ProcessingAnimation = value);
    }

    bool _grammarEnabled;
    public bool GrammarEnabled
    {
        get => _grammarEnabled;
        set
        {
            if (SyncSetting(ref _grammarEnabled, value,
                    () => _state.GrammarCheckEnabled = value,
                    value ? "Grammar check enabled" : "Grammar check disabled"))
                RefreshGrammarService();
        }
    }

    string _grammarApiHost = "";
    public string GrammarApiHost
    {
        get => _grammarApiHost;
        set
        {
            if (SyncTextSetting(ref _grammarApiHost, value, () => _state.GrammarApiEndpoint = value))
                RefreshGrammarService();
        }
    }

    string _grammarApiKey = "";
    public string GrammarApiKey
    {
        get => _grammarApiKey;
        set
        {
            if (SyncTextSetting(ref _grammarApiKey, value, () => _state.GrammarApiKey = value))
                RefreshGrammarService();
        }
    }

    string _grammarEffort = "";
    public string GrammarEffort
    {
        get => _grammarEffort;
        set
        {
            if (SyncTextSetting(ref _grammarEffort, value, () => _state.GrammarReasoningEffort = value))
                RefreshGrammarService();
        }
    }

    public IReadOnlyList<GrammarEffortOption> GrammarEffortOptions { get; } = GrammarCheckService.EffortOptions;

    string _grammarStatus = "";
    public string GrammarStatus
    {
        get => _grammarStatus;
        set => SetProperty(ref _grammarStatus, value);
    }

    GrammarCheckService? _grammar;
    string? _grammarSignature;
    bool _isProbingGrammar;

    void RefreshGrammarService()
    {
        var signature = $"{_grammarApiHost}|{_grammarApiKey}|{_grammarEffort}";
        if (_grammar == null || _grammarSignature != signature)
        {
            _grammar?.Dispose();
            _grammar = null;
            _grammarSignature = null;

            if (!string.IsNullOrWhiteSpace(_grammarApiHost))
            {
                _grammar = new GrammarCheckService(_grammarApiHost, _grammarApiKey, _grammarEffort);
                _grammarSignature = signature;
            }
        }

        UpdateGrammarStatus();

        if (_grammarEnabled && _grammar is { DetectedModel: null } && !_isProbingGrammar)
            FireAndForget(ProbeGrammarAsync(), "GrammarProbe");
    }

    async Task ProbeGrammarAsync()
    {
        var service = _grammar;
        if (service == null)
            return;

        _isProbingGrammar = true;
        UpdateGrammarStatus();
        try
        {
            await service.ProbeAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"[GrammarProbe] failed: {ex.Message}");
        }
        finally
        {
            _isProbingGrammar = false;
            UpdateGrammarStatus();
        }
    }

    enum GrammarState { Off, NoHost, Probing, Ready, Unchecked, Problem }
    GrammarState _grammarState;

    static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static readonly Brush GrammarIdleBrush = FrozenBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
    static readonly Brush GrammarReadyBrush = FrozenBrush(Color.FromRgb(0x3F, 0xAE, 0x65));
    static readonly Brush GrammarProbingBrush = FrozenBrush(Color.FromRgb(0xC8, 0xA2, 0x4B));
    static readonly Brush GrammarProblemBrush = FrozenBrush(Color.FromRgb(0xC9, 0x52, 0x5A));

    public Brush GrammarStateBrush => _grammarState switch
    {
        GrammarState.Ready => GrammarReadyBrush,
        GrammarState.Probing => GrammarProbingBrush,
        GrammarState.Problem => GrammarProblemBrush,
        _ => GrammarIdleBrush,
    };

    void UpdateGrammarStatus()
    {
        string text;
        if (!_grammarEnabled)
        {
            _grammarState = GrammarState.Off;
            text = "Grammar check off";
        }
        else if (_grammar == null)
        {
            _grammarState = GrammarState.NoHost;
            text = "Set an API host to start checking";
        }
        else if (_isProbingGrammar)
        {
            _grammarState = GrammarState.Probing;
            text = "Checking grammar API...";
        }
        else if (_grammar.DetectedModel != null)
        {
            _grammarState = GrammarState.Ready;
            text = _grammar.DetectedModel == GrammarCheckService.FallbackModel
                ? $"Grammar API ready ({GrammarCheckService.FallbackModel} model)"
                : $"Grammar API ready (model: {_grammar.DetectedModel})";
        }
        else
        {
            _grammarState = _grammar.ProbeError == null ? GrammarState.Unchecked : GrammarState.Problem;
            text = _grammar.ProbeError == null
                ? "Grammar API not checked yet"
                : $"Skipped: {_grammar.ProbeError}";
        }

        GrammarStatus = text;
        OnPropertyChanged(nameof(GrammarStateBrush));
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

    int _hotkeyVirtualKeyCode;
    public int HotkeyVirtualKeyCode
    {
        get => _hotkeyVirtualKeyCode;
        set
        {
            if (SetProperty(ref _hotkeyVirtualKeyCode, value))
            {
                _state.HotkeyVirtualKeyCode = value;
                InstallHook();
            }
        }
    }

    int _micDeviceNumber;
    string? _micDeviceName;
    public int MicDeviceNumber => _micDeviceNumber;

    public void SetMicSelection(int deviceNumber, string? deviceName)
    {
        if (_micDeviceNumber == deviceNumber && string.Equals(_micDeviceName, deviceName, StringComparison.Ordinal))
            return;

        _micDeviceNumber = deviceNumber;
        _micDeviceName = deviceName;
        _state.MicDeviceNumber = deviceNumber;
        _state.MicDeviceName = deviceName;
        RecordingManager.SetMicDevice(deviceNumber, deviceName);
    }

    string _hotkeyName = "";
    public string HotkeyName
    {
        get => _hotkeyName;
        set => SetProperty(ref _hotkeyName, value);
    }

    public ICommand ServerCommand { get; }
    public ICommand DownloadModelCommand { get; }

    public MainWindowViewModel(AppState state)
    {
        _state = state;
        ServerManager = new ServerStateManager(state);
        RecordingManager = new RecordingStateManager();
        _queue = new TranscriptionQueue(ProcessQueuedJobAsync);
        _queue.Changed += (_, _) =>
            RecordingManager.SetQueueState(_queue.WaitingCount, _queue.OutstandingCount);
        WireRecordingEvents();
        SeedSettingsFromState();
        RecordingManager.SetMicDevice(_micDeviceNumber, _micDeviceName);
        UpdateHardwareMode();

        CheckModelExists();

        ServerCommand = new RelayCommand(_ => FireAndForget(
            ServerManager.ToggleServerAsync(s => RecordingManager.InfoText = s ?? ""),
            "ToggleServer"));
        DownloadModelCommand = new RelayCommand(_ => FireAndForget(DownloadModelAsync(), "DownloadModel"));

        InstallHook();

        FireAndForget(InitializeAsync(), "Initialize");
    }

    void WireRecordingEvents()
    {
        ServerManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerManager.Status))
                UpdateHardwareMode();
        };
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
            if (e.PropertyName == nameof(RecordingStateManager.Result) &&
                RecordingManager.Result == RecordingResult.Success)
            {
                SetHighlighted(true);
                _highlightTimer.Stop();
                _highlightTimer.Start();
            }
        };
    }

    void SeedSettingsFromState()
    {
        _autoOffloadVram = _state.AutoOffloadVram;
        _useCpuOnly = _state.UseCpuOnly;
        _startupEnabled = _state.StartupEnabled;
        _autoPaste = _state.AutoPaste;
        _minimizeToTray = _state.MinimizeToTray;
        _startInTray = _state.StartInTray;
        _selectionToolbarEnabled = _state.SelectionToolbarEnabled;
        _visualizerEnabled = _state.VisualizerEnabled;
        _visualizerStyle = _state.VisualizerStyle;
        _visualizerPalette = _state.VisualizerPalette;
        _visualizerFps = _state.VisualizerFps;
        _processingAnimation = _state.ProcessingAnimation;
        _grammarEnabled = _state.GrammarCheckEnabled;
        _grammarApiHost = _state.GrammarApiEndpoint;
        _grammarApiKey = _state.GrammarApiKey;
        _grammarEffort = _state.GrammarReasoningEffort;
        RefreshGrammarService();
        SyncStartupEntry();
        _hotkeyVirtualKeyCode = _state.HotkeyVirtualKeyCode;
        _hotkeyName = HotkeyCatalog.NameOf(_state.HotkeyVirtualKeyCode);
        _micDeviceNumber = _state.MicDeviceNumber;
        _micDeviceName = _state.MicDeviceName;
        AnchorMicDeviceName();

        // The block above writes fields directly so no setting-change side effect fires
        // during load, but the recorder's spectrum tap starts active: without this a
        // persisted VisualizerEnabled=false still pays the FFT on every capture.
        RecordingManager.SetVisualizerActive(_visualizerEnabled);
    }

    void SyncStartupEntry()
    {
        if (!_startupEnabled && StartupRegistry.IsEnabled())
        {
            _startupEnabled = true;
            _state.StartupEnabled = true;
        }
        else if (_startupEnabled && !StartupRegistry.IsEnabled())
        {
            Logger.Warn("Startup entry missing or stale; recreating it");
            StartupRegistry.SetEnabled(true);
        }
    }

    void AnchorMicDeviceName()
    {
        if (_micDeviceNumber < 0 || !string.IsNullOrEmpty(_micDeviceName))
            return;
        foreach (var (number, name) in AudioRecorder.GetInputDevices())
        {
            if (number != _micDeviceNumber)
                continue;
            _micDeviceName = name;
            _state.MicDeviceName = name;
            break;
        }
    }

    public void ApplySettings(SettingsSnapshot settings)
    {
        var cpuModeChanged = _state.UseCpuOnly != settings.UseCpuOnly;
        var localModelChanged = _state.SetLocalModel(settings.LocalModelId);

        AutoOffloadVram = settings.AutoOffloadVram;
        UseCpuOnly = settings.UseCpuOnly;
        StartupEnabled = settings.StartupEnabled;
        AutoPaste = settings.AutoPaste;
        MinimizeToTray = settings.MinimizeToTray;
        StartInTray = settings.StartInTray;
        HotkeyVirtualKeyCode = settings.HotkeyVirtualKeyCode;
        // Order matters: the enable toggle probes the currently set endpoint.
        GrammarApiHost = settings.GrammarApiHost;
        GrammarApiKey = settings.GrammarApiKey;
        GrammarEffort = settings.GrammarEffort;
        GrammarEnabled = settings.GrammarEnabled;

        SelectionToolbarEnabled = settings.SelectionToolbarEnabled;
        VisualizerEnabled = settings.VisualizerEnabled;
        VisualizerStyle = settings.VisualizerStyle;
        VisualizerPalette = settings.VisualizerPalette;
        VisualizerFps = settings.VisualizerFps;
        ProcessingAnimation = settings.ProcessingAnimation;

        if (cpuModeChanged && ServerManager.IsLocal && ServerManager.IsServerRunning)
            FireAndForget(ServerManager.StopServerAsync(), "StopServerForCpuMode");

        if (localModelChanged && ServerManager.IsLocal && ServerManager.IsServerRunning)
            FireAndForget(ServerManager.StopServerAsync(), "StopServerForModelChange");
    }

    async Task InitializeAsync()
    {
        Logger.Info("Available microphones:");
        AudioRecorder.LogAvailableDevices();
        Logger.Info("App started");
        FireAndForget(RecordingManager.WarmUpMic(), "WarmUpMic");
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
                if (RecordingManager.CanStartRecording)
                {
                    _hotkeyPressed = true;
                    _pendingHotkeyStop = false;
                    _captureStarting = true;
                    FireAndForget(StartHoldRecord(true), "StartHoldRecord");
                }
                else
                {
                    // Normal while the previous clip is still being transcribed:
                    // the user simply waits for the mic to free up and presses again.
                    Logger.Info(RecordingManager.IsRecording
                        ? "Hotkey press ignored: the microphone is still capturing"
                        : "Hotkey press ignored: the processing queue is full");
                    if (!RecordingManager.IsRecording)
                        RecordingManager.InfoText = "Queue is full — wait for the queued recordings";
                }
                await Task.CompletedTask;
            },
            async () =>
            {
                _hotkeyPressed = false;
                if (RecordingManager.IsRecording)
                {
                    // The pending-stop fallback only applies while this press's
                    // capture is still coming up; see _captureStarting.
                    await StopHoldRecord(queueIfBusy: _captureStarting);
                }
            }
        );
        HotkeyName = HotkeyCatalog.NameOf(_hotkeyVirtualKeyCode);
        RecordingManager.InfoText = $"Hotkey: {HotkeyName}";
    }

    public async Task StartHoldRecord(bool isHotkey = true)
    {
        // The selection fix owns the clipboard for seconds (copy probe, corrected
        // text, restore); starting dictation in that window would let its
        // auto-paste interleave with the fix's Ctrl+V and corrupt the document.
        if (_fixSelectionRunning)
        {
            Logger.Info("Dictation start ignored: a selection fix is running");
            RecordingManager.InfoText = "Selection fix in progress — start dictation in a moment";
            return;
        }

        // A press that arrives while the previous capture is still stopping or
        // resampling waits for the mic instead of being dropped.
        try
        {
            await RunRecordingOperation(
                () => StartHoldRecordCore(isHotkey),
                "StartHoldRecord",
                waitForMic: true);
        }
        finally
        {
            _captureStarting = false;
        }
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
            await StopAndEnqueueCore();
        }
    }

    public async Task StopHoldRecord(bool queueIfBusy = false)
    {
        await RunRecordingOperation(
            StopAndEnqueueCore,
            "StopHoldRecord",
            queueHotkeyStop: queueIfBusy,
            failureInfo: "Failed to stop recording");
    }

    // Releasing the hotkey only hands the clip to the queue; the transcription of
    // this clip and of every clip recorded after it runs one at a time, in order.
    async Task StopAndEnqueueCore()
    {
        var pcm = await RecordingManager.StopRecording();
        // Read the mic format at stop time: the device can be reopened or
        // re-selected before this job gets its turn.
        var channels = RecordingManager.ChannelCount;

        if (!ValidateRecording(pcm))
            return;

        if (!_queue.TryEnqueue(new QueuedAudio(pcm, channels)))
        {
            Logger.Warn("Recording dropped: the processing queue is full");
            ShowFailure("Too many recordings waiting — let them finish first");
        }
    }

    async Task RunRecordingOperation(
        Func<Task> operation,
        string context,
        bool waitForMic = false,
        bool queueHotkeyStop = false,
        string? failureInfo = null)
    {
        var acquired = waitForMic
            ? await _captureLock.WaitAsync(CaptureWaitTimeout)
            : await _captureLock.WaitAsync(0);

        if (!acquired)
        {
            if (queueHotkeyStop)
                _pendingHotkeyStop = true;
            else
            {
                Logger.Info($"[{context}] ignored: the microphone is still busy");
                RecordingManager.InfoText = "Microphone is still busy — try again";
            }
            return;
        }

        try
        {
            // Two quick presses can both pass the hotkey gate; the one that had
            // to wait for the mic must not double-start the capture.
            if (waitForMic && !RecordingManager.CanStartRecording)
            {
                Logger.Info($"[{context}] ignored: the capture already started");
                return;
            }

            await operation();
        }
        catch (OperationCanceledException)
        {
            RecordingManager.Reset();
        }
        catch (Exception ex)
        {
            Logger.Error($"[{context}] exception: {ex.Message}");
            RecordingManager.Reset();
            var info = ShortenForPill(ex.Message);
            if (string.IsNullOrWhiteSpace(info))
                info = failureInfo;
            if (!string.IsNullOrEmpty(info))
                RecordingManager.InfoText = info;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    static string ShortenForPill(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var clean = message;
        var noise = clean.IndexOf("(Exception from HRESULT:", StringComparison.OrdinalIgnoreCase);
        if (noise > 0)
            clean = clean[..noise];

        var line = clean.Split('\n')[0].Trim();
        return line.Length <= 120 ? line : line[..120].TrimEnd() + "...";
    }

    void ShowFailure(string message) => RecordingManager.ReportError(message);

    const double MinUsefulRecordingSeconds = 0.25;

    string MicLabel => RecordingManager.MicrophoneName is { Length: > 0 } name
        ? $"\u201c{name}\u201d"
        : "the selected mic";

    // Checked the moment the clip is captured, so a bad take reports at once
    // instead of after the recordings queued in front of it.
    bool ValidateRecording(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            Logger.Warn("Verdict: the capture produced no audio at all");
            ShowFailure($"Microphone captured nothing — check {MicLabel}");
            return false;
        }

        var durationSec = pcm.Length / (double)(AppConfig.SampleRate * AppConfig.BitsPerSample / 8);
        if (durationSec < MinUsefulRecordingSeconds)
        {
            Logger.Warn($"Verdict: recording too short ({durationSec:F2}s)");
            ShowFailure("Recording was too short — hold the key while speaking");
            return false;
        }

        var maxAmplitude = AudioProcessor.GetMaxAmplitude(pcm);
        if (maxAmplitude < AudioProcessor.SilentPeakFloor)
        {
            Logger.Warn($"Verdict: no signal from {MicLabel} (peak {maxAmplitude})");
            ShowFailure($"Microphone got no signal — check {MicLabel}");
            return false;
        }

        Logger.Info($"Captured {durationSec:F1} s (peak {maxAmplitude}); queued for processing");
        return true;
    }

    // Runs one clip at a time, in recording order, on the queue's worker.
    async Task ProcessQueuedJobAsync(QueuedAudio job, CancellationToken ct)
    {
        var provider = _state.ActiveProvider;
        if (provider != null && provider.IsLocal && !await ServerManager.IsServerReady())
        {
            RecordingManager.InfoText = "Waiting for server...";
            if (ServerManager.IsStarting)
            {
                if (!await ServerManager.WaitForServerReady(s => RecordingManager.InfoText = s))
                {
                    ShowFailure("Server failed to start — try again");
                    return;
                }
            }
            else if (!await EnsureServerStartedAsync())
                return;
        }

        await TranscribeAndHandleResultAsync(job.Pcm, job.Channels, ct);

        // Offload only when nothing is waiting behind this job (this job still
        // counts as running, so Busy would always be true here). Dropping the
        // model between two queued clips would cost a server restart per message.
        if (_queue.WaitingCount == 0)
            await OffloadServerAfterSuccessAsync();
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
            ShowFailure(ex.Message);
            return false;
        }
    }

    static string FormatProgressMessage(string message, long downloaded, long total) =>
        total > 0
            ? $"{message} ({ModelDownloader.FormatBytes(downloaded)}/{ModelDownloader.FormatBytes(total)})"
            : message;

    async Task TranscribeAndHandleResultAsync(byte[] pcm, int channels, CancellationToken ct)
    {
        RecordingManager.InfoText = "Sending to LLM...";

        try
        {
            var text = await ServerManager.TranscribeAsync(pcm, channels, ct);

            if (ct.IsCancellationRequested)
            {
                RecordingManager.Reset();
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                var (final, grammarNote) = await ProofreadAsync(text, ct);
                await ApplyTranscriptionSuccessAsync(final, ct, grammarNote);
            }
            else
            {
                ShowFailure(DescribeTranscriptionFailure(ServerManager.LastTranscriptionFailure));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Error($"[ProcessAudio] transcription request timed out after {TranscriptionService.TimeoutDescription}");
            ShowFailure($"Model did not respond in {TranscriptionService.TimeoutDescription} — try again");
        }
        catch (OperationCanceledException)
        {
            RecordingManager.Reset();
        }
        catch (Exception ex)
        {
            Logger.Error($"[ProcessAudio] exception: {ex.Message}");
            ShowFailure(ex.Message);
        }
    }

    string DescribeTranscriptionFailure(AsrFailure failure) => failure switch
    {
        AsrFailure.NoSpeech =>
            $"Did not hear speech through {MicLabel} — speak up or move closer",
        AsrFailure.ForeignLanguage =>
            "The model answered in another language — nothing was pasted",
        _ => "Model returned no transcription — try again"
    };

    async Task<(string text, string? grammarNote)> ProofreadAsync(string text, CancellationToken ct)
    {
        var service = _grammar;
        if (!_grammarEnabled)
            return (text, null);
        if (service == null)
            return (text, "no grammar API host set");

        RecordingManager.InfoText = "Proofreading...";
        try
        {
            var corrected = await service.CorrectAsync(text, ct);
            if (!string.IsNullOrWhiteSpace(corrected))
                return (corrected, null);

            return (text, "grammar API unavailable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Error("[Grammar] request timed out; using the original text");
            return (text, "grammar API answered too late");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Grammar] failed: {ex.Message}");
            return (text, "grammar check failed");
        }
    }

    async Task ApplyTranscriptionSuccessAsync(string text, CancellationToken ct, string? grammarNote = null)
    {
        // Never write the transcript to the clipboard while a selection fix is
        // mid-flow; queue behind its clipboard window instead of racing it.
        await WaitForSelectionFixIdleAsync();
        var copied = await TrySetClipboardTextAsync(text);
        bool pasted = false;
        string? pasteFailure = null;

        if (copied && _autoPaste)
        {
            var (aborted, pastedNow, failure) = await TryAutoPasteAsync(ct);
            if (aborted)
                return;
            pasted = pastedNow;
            pasteFailure = failure;
        }

        var note = grammarNote == null ? "" : $" · {grammarNote}";
        RecordingManager.ReportSuccess(text);
        if (!copied)
            RecordingManager.InfoText = "Transcribed, but clipboard was unavailable" + note;
        else if (pasted)
            RecordingManager.InfoText = "Copied and pasted" + note;
        else if (_autoPaste)
            RecordingManager.InfoText = $"Copied, but auto-paste failed ({pasteFailure})" + note;
        else if (note.Length > 0)
            RecordingManager.InfoText = "Copied to clipboard" + note;

        NotificationSound.Play();
    }

    async Task<(bool aborted, bool pasted, string? failure)> TryAutoPasteAsync(CancellationToken ct)
    {
        // Wait for the clipboard to settle and key-up to land before Ctrl+V.
        await Task.Delay(100);
        if (ct.IsCancellationRequested)
        {
            RecordingManager.Reset();
            return (true, false, null);
        }

        var result = AutoPaster.SendCtrlV();
        if (!result.Success)
        {
            // The failed first attempt injected nothing, so a retry cannot double-paste.
            await Task.Delay(250);
            if (!ct.IsCancellationRequested)
                result = AutoPaster.SendCtrlV();
        }

        return (false, result.Success, result.Success ? null : result.ShortReason);
    }

    // UI-thread-only flag; read cross-thread only via WaitForSelectionFixIdleAsync.
    volatile bool _fixSelectionRunning;

    // The user's clipboard snapshot that the fix replaced and has not restored
    // yet. Kept here so app exit mid-flow can write it back synchronously.
    string? _pendingClipboardRestore;

    public void FlushPendingClipboardRestore()
    {
        var pending = _pendingClipboardRestore;
        if (pending == null)
            return;
        _pendingClipboardRestore = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(pending);
                return;
            }
            catch (Exception ex) when (ex.HResult == ClipbrdECantOpen && attempt < 4)
            {
                Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                // Runs on MainWindow_Closed's stack: an escaping throw skips tray/overlay
                // teardown and the explicit dispatcher shutdown. Only CLIPBRD_E_CANT_OPEN
                // is transient; give up quietly on everything else.
                Logger.Error($"Selection fix: could not restore the clipboard before exit: {ex.Message}");
                return;
            }
        }
        Logger.Error("Selection fix: could not restore the clipboard before exit");
    }

    async Task WaitForSelectionFixIdleAsync()
    {
        for (var i = 0; i < 60 && _fixSelectionRunning; i++)
            await Task.Delay(100);
    }

    // Fixes the text the user selected in whatever app has focus: Ctrl+C through
    // AutoPaster, the shared GrammarCheckService, then Ctrl+V over the selection.
    // ProofreadAsync (transcript) is untouched; both flows share the same service.
    // targetWindow is the window that held the selection; injected keys must land
    // there, so focus is pulled back from WhisperNote's own popup when needed.
    public async Task<bool> FixSelectedTextAsync(IntPtr targetWindow = default)
    {
        Logger.Info("Selection fix: requested");
        if (SelectionFixer.IsBlocked(_fixSelectionRunning,
                RecordingManager.IsRecording, RecordingManager.IsProcessing))
        {
            Logger.Info("Selection fix ignored: " +
                (_fixSelectionRunning ? "another fix is running" :
                 RecordingManager.IsRecording ? "recording is in progress" :
                 "the transcription queue is busy"));
            return false;
        }

        _fixSelectionRunning = true;
        try
        {
            return await FixSelectedTextCoreAsync(targetWindow);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SelectionFix] failed: {ex.Message}");
            ShowFixStatus("Selection fix", "Red", "fix failed, see the log");
            return false;
        }
        finally
        {
            _pendingClipboardRestore = null;
            _fixSelectionRunning = false;
        }
    }

    async Task<bool> FixSelectedTextCoreAsync(IntPtr targetWindow)
    {
        var service = _grammar;
        if (service == null)
        {
            Logger.Info("Selection fix skipped: no grammar API host set");
            ShowFixStatus("Selection fix", "Red", "no grammar API host set");
            return false;
        }

        // Against an elevated target SendInput "succeeds" while UIPI silently drops
        // the keys: bail out before touching the clipboard, so the user's copy and
        // clipboard both survive instead of faking a fix.
        if (AutoPaster.IsTargetElevatedBlocked(targetWindow))
        {
            Logger.Info("Selection fix skipped: the target app runs elevated (injected keys would be dropped)");
            ShowFixStatus("Selection fix", "Red", "target app runs as administrator");
            return false;
        }

        ShowFixStatus("Fixing selected text...", "LightBlue", "");

        var snapshot = await TryGetClipboardTextAsync();
        _pendingClipboardRestore = snapshot;
        // A clipboard-diff probe can't prove a copy happened when the clipboard already
        // holds the very text being copied (re-fixing the same selection); seed a
        // sentinel so any successful Ctrl+C is detectable. Falls back to diffing when
        // the clipboard is non-text and the sentinel cannot be written.
        var sentinel = $"__whispernote_copy_probe_{Environment.TickCount:X}__";
        var probeSent = await TrySetClipboardTextAsync(sentinel) || await TrySetClipboardTextAsync(sentinel);
        var probe = snapshot != null && probeSent ? sentinel : snapshot;
        if (AutoPaster.ReclaimForeignForeground(targetWindow))
            Logger.Info("Selection fix: focus was on a WhisperNote window, restored the target app");
        var copyInjected = AutoPaster.SendCtrlC();
        Logger.Info($"Selection fix: Ctrl+C injected={copyInjected}, clipboard snapshot length={(snapshot?.Length ?? -1)}");
        if (!copyInjected)
        {
            if (probe == sentinel && !await TrySetClipboardTextAsync(snapshot!))
                Logger.Error("Selection fix: clipboard restore failed; a probe placeholder may remain");
            ShowFixStatus("Selection fix", "Red", "could not copy the selection");
            return false;
        }

        var selected = await WaitForCopiedSelectionAsync(probe);
        if (selected == null)
        {
            // Clipboard unchanged: nothing was selected, or the field refuses Ctrl+C.
            Logger.Info("Selection fix: clipboard unchanged after Ctrl+C (nothing selected or copy refused)");
            if (probe != null && probe != snapshot && !await TrySetClipboardTextAsync(snapshot!))
                Logger.Error("Selection fix: clipboard restore failed; a probe placeholder may remain");
            ShowFixStatus("Selection fix", "Red", "Nothing selected to fix");
            return false;
        }
        if (string.IsNullOrWhiteSpace(selected))
        {
            // CorrectAsync short-circuits on whitespace; report it honestly instead
            // of blaming the API.
            Logger.Info($"Selection fix: copied selection is whitespace only ({selected.Length} chars)");
            if (snapshot != null && selected != snapshot)
                await TrySetClipboardTextAsync(snapshot);
            ShowFixStatus("Selection fix", "Red", "Nothing selected to fix");
            return false;
        }
        Logger.Info($"Selection fix: copied {selected.Length} chars, correcting");

        string lastWritten = selected;
        string? corrected = null;
        try
        {
            corrected = await service.CorrectAsync(selected);
        }
        catch (OperationCanceledException) { }

        if (string.IsNullOrWhiteSpace(corrected))
        {
            await RestoreClipboardIfNeededAsync(snapshot, lastWritten);
            ShowFixStatus("Selection fix", "Red", "grammar API unavailable");
            return false;
        }

        if (corrected == selected)
        {
            await RestoreClipboardIfNeededAsync(snapshot, lastWritten);
            ShowFixStatus("Selection fix", "Green", "Already correct");
            return true;
        }

        if (!await TrySetClipboardTextAsync(corrected))
        {
            await RestoreClipboardIfNeededAsync(snapshot, lastWritten);
            ShowFixStatus("Selection fix", "Red", "clipboard was unavailable");
            return false;
        }
        lastWritten = corrected;

        await Task.Delay(100);
        AutoPaster.ReclaimForeignForeground(targetWindow);
        var result = AutoPaster.SendCtrlV();
        if (!result.Success)
        {
            await Task.Delay(250);
            result = AutoPaster.SendCtrlV();
        }

        if (result.Success)
        {
            Logger.Info($"Selection fix: pasted {corrected.Length} chars over the selection");
            NotificationSound.Play();
            ShowFixStatus("Selection fix", "Green", "Grammar fixed");
            await RestoreClipboardIfNeededAsync(snapshot, lastWritten);
            return true;
        }

        // The corrected text stays on the clipboard as the manual-paste fallback.
        Logger.Error($"Selection fix: Ctrl+V not accepted by the target app ({result.Detail})");
        ShowFixStatus("Selection fix", "Red", $"corrected text is on the clipboard ({result.ShortReason})");
        return false;
    }

    // The target app fills the clipboard a moment after the injected Ctrl+C.
    async Task<string?> WaitForCopiedSelectionAsync(string? snapshot)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(60);
            var current = await TryGetClipboardTextAsync();
            if (SelectionFixer.ClipboardChanged(snapshot, current))
                return current;
            if (attempt == 7)
                Logger.Info($"Selection fix: clipboard poll saw snapshot length={(snapshot?.Length ?? -1)}, current length={(current?.Length ?? -1)}");
        }
        return null;
    }

    async Task RestoreClipboardIfNeededAsync(string? snapshot, string lastWritten)
    {
        if (snapshot == null)
            return;

        // Slow targets (Chromium async paste, Office re-layout, RDP) read the
        // clipboard well after Ctrl+V lands; restoring earlier hands them the
        // user's OLD clipboard text to paste over the selection.
        await Task.Delay(1200);
        var current = await TryGetClipboardTextAsync();
        if (current != null && SelectionFixer.ShouldRestore(current, lastWritten)
            && !await TrySetClipboardTextAsync(snapshot))
            Logger.Error("Selection fix: clipboard restore failed after a successful paste");
    }

    void ShowFixStatus(string status, string kind, string info)
    {
        RecordingManager.StatusText = status;
        RecordingManager.StatusTextKind = kind;
        RecordingManager.InfoText = info;
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

    const int ClipboardRetryAttempts = 5;
    const int ClipboardRetryDelayMs = 100;
    static readonly int ClipbrdECantOpen = unchecked((int)0x800401D0);

    static async Task<bool> TrySetClipboardTextAsync(string text)
    {
        // The clipboard is a system-wide lock held by whichever process opened it.
        // CLIPBRD_E_CANT_OPEN means somebody (Ctrl+V in the target app, Win+V history,
        // clipboard managers, RDP) is holding it right now; WPF tries once and gives up,
        // so retry briefly as Microsoft's docs recommend. Other errors are not transient.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex) when (ex.HResult == ClipbrdECantOpen && attempt < ClipboardRetryAttempts)
            {
                Logger.Error($"Clipboard.SetText attempt {attempt} hit a locked clipboard ({ex.Message}); retrying");
                await Task.Delay(ClipboardRetryDelayMs);
            }
            catch (Exception ex)
            {
                Logger.Error($"Clipboard.SetText failed: {ex.Message}");
                return false;
            }
        }
    }

    static async Task<string?> TryGetClipboardTextAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception ex) when (ex.HResult == ClipbrdECantOpen && attempt < ClipboardRetryAttempts)
            {
                await Task.Delay(ClipboardRetryDelayMs);
            }
            catch (Exception ex)
            {
                Logger.Error($"Clipboard.GetText failed: {ex.Message}");
                return null;
            }
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
        // Cancels the running job and drops the queued ones, so nothing below is
        // touched by a job after it has been disposed.
        _queue.Dispose();
        _highlightTimer.Stop();
        _captureLock.Dispose();
        _keyboardHook?.Dispose();
        ServerManager.Dispose();
        RecordingManager.Dispose();
        _grammar?.Dispose();
        _grammar = null;
    }
}
