using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote.ViewModels;

public sealed class SettingsViewModel : ViewModel
{
    readonly MainWindowViewModel _mainViewModel;
    bool _autoOffloadVram;
    bool _useCpuOnly;
    bool _startupEnabled;
    bool _autoPaste;
    bool _minimizeToTray;
    bool _startInTray;
    string? _localModelId;
    int _hotkeyVirtualKeyCode;
    int _micDeviceNumber;
    bool _grammarEnabled;
    string _grammarApiHost = "";
    string _grammarApiKey = "";
    string _grammarEffort = "";
    bool _selectionToolbarEnabled;
    bool _visualizerEnabled = true;
    string _visualizerStyle = VisualizerCatalog.DefaultStyleName;
    string _visualizerPalette = VisualizerPaletteCatalog.DefaultPaletteName;
    readonly PreviewVoiceSource _previewSource = new();

    public bool AutoOffloadVram
    {
        get => _autoOffloadVram;
        set => SetProperty(ref _autoOffloadVram, value);
    }

    public bool UseCpuOnly
    {
        get => _useCpuOnly;
        set => SetProperty(ref _useCpuOnly, value);
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set => SetProperty(ref _startupEnabled, value);
    }

    public bool AutoPaste
    {
        get => _autoPaste;
        set => SetProperty(ref _autoPaste, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool StartInTray
    {
        get => _startInTray;
        set => SetProperty(ref _startInTray, value);
    }

    public IReadOnlyList<LocalModelOption> LocalModelOptions { get; } = LocalModels.All;
    public string? LocalModelId
    {
        get => _localModelId;
        set => SetProperty(ref _localModelId, value);
    }

    public int HotkeyVirtualKeyCode
    {
        get => _hotkeyVirtualKeyCode;
        set => SetProperty(ref _hotkeyVirtualKeyCode, value);
    }

    public IReadOnlyList<HotkeyOption> HotkeyOptions { get; }

    public IReadOnlyList<MicOption> MicOptions { get; }
    public int MicDeviceNumber
    {
        get => _micDeviceNumber;
        set
        {
            if (!SetProperty(ref _micDeviceNumber, value))
                return;

            string? name = null;
            if (value >= 0)
                name = MicOptions.FirstOrDefault(option => option.Number == value)?.Name;
            _mainViewModel.SetMicSelection(value, name);
        }
    }

    public bool GrammarEnabled
    {
        get => _grammarEnabled;
        set => SetProperty(ref _grammarEnabled, value);
    }

    public string GrammarApiHost
    {
        get => _grammarApiHost;
        set => SetProperty(ref _grammarApiHost, value);
    }

    public string GrammarApiKey
    {
        get => _grammarApiKey;
        set => SetProperty(ref _grammarApiKey, value);
    }

    public string GrammarEffort
    {
        get => _grammarEffort;
        set => SetProperty(ref _grammarEffort, value);
    }

    public IReadOnlyList<GrammarEffortOption> GrammarEffortOptions { get; } = GrammarCheckService.EffortOptions;

    public bool SelectionToolbarEnabled
    {
        get => _selectionToolbarEnabled;
        set => SetProperty(ref _selectionToolbarEnabled, value);
    }

    public bool VisualizerEnabled
    {
        get => _visualizerEnabled;
        set
        {
            if (SetProperty(ref _visualizerEnabled, value))
                UpdatePreviewPump();
        }
    }

    // The synthetic voice signal only has to run while the preview is on screen.
    bool _previewTabVisible;

    public void SetPreviewTabVisible(bool visible)
    {
        _previewTabVisible = visible;
        UpdatePreviewPump();
    }

    void UpdatePreviewPump() => _previewSource.Active = _previewTabVisible && _visualizerEnabled;

    public IReadOnlyList<VisualizerOption> VisualizerStyleOptions { get; } = VisualizerCatalog.Options;

    // Carries the option's display name; VisualizerCatalog.Resolve maps it back.
    public string VisualizerStyle
    {
        get => _visualizerStyle;
        set
        {
            if (SetProperty(ref _visualizerStyle, value))
                OnPropertyChanged(nameof(PreviewStyle));
        }
    }

    public PreviewVoiceSource PreviewSource => _previewSource;

    // The same enum the overlay receives, so the preview cannot drift from the setting.
    public Config.VisualizerStyle PreviewStyle => VisualizerCatalog.Resolve(_visualizerStyle);

    public IReadOnlyList<VisualizerPalette> VisualizerPaletteOptions { get; } = VisualizerPaletteCatalog.Options;

    // Also a display name kept as text, so an unknown saved name falls back to the default scheme.
    public string VisualizerPalette
    {
        get => _visualizerPalette;
        set
        {
            if (SetProperty(ref _visualizerPalette, value))
                OnPropertyChanged(nameof(PreviewPalette));
        }
    }

    public VisualizerPalette PreviewPalette => VisualizerPaletteCatalog.Resolve(_visualizerPalette);

    public string GrammarStatus => _mainViewModel.GrammarStatus;

    public Brush GrammarStateBrush => _mainViewModel.GrammarStateBrush;

    public SettingsViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _autoOffloadVram = mainViewModel.AutoOffloadVram;
        _useCpuOnly = mainViewModel.UseCpuOnly;
        _startupEnabled = mainViewModel.StartupEnabled;
        _autoPaste = mainViewModel.AutoPaste;
        _minimizeToTray = mainViewModel.MinimizeToTray;
        _startInTray = mainViewModel.StartInTray;
        _localModelId = mainViewModel.LocalModelId ?? LocalModels.DefaultId;
        _hotkeyVirtualKeyCode = mainViewModel.HotkeyVirtualKeyCode;
        HotkeyOptions = CreateHotkeyOptions(_hotkeyVirtualKeyCode);
        _micDeviceNumber = mainViewModel.MicDeviceNumber;
        MicOptions = CreateMicOptions(_micDeviceNumber);
        _grammarEnabled = mainViewModel.GrammarEnabled;
        _grammarApiHost = mainViewModel.GrammarApiHost;
        _grammarApiKey = mainViewModel.GrammarApiKey;
        _grammarEffort = mainViewModel.GrammarEffort;
        _selectionToolbarEnabled = mainViewModel.SelectionToolbarEnabled;
        _visualizerEnabled = mainViewModel.VisualizerEnabled;
        // Normalized so the combo always shows one of its own option names.
        _visualizerStyle = VisualizerCatalog.NameOf(VisualizerCatalog.Resolve(mainViewModel.VisualizerStyle));
        _visualizerPalette = VisualizerPaletteCatalog.NameOf(VisualizerPaletteCatalog.Resolve(mainViewModel.VisualizerPalette));

        mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

        PropertyChanged += (_, e) =>
        {
            // MicDeviceNumber commits through SetMicSelection instead. PreviewStyle and
            // PreviewPalette ride along with Style/Palette, and applying twice would
            // write the config file twice for one pick.
            var name = e.PropertyName;
            if (name != nameof(MicDeviceNumber) && name != nameof(PreviewStyle) && name != nameof(PreviewPalette))
                Apply();
        };
    }

    public void Detach()
    {
        _previewSource.Dispose();
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
    }

    void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GrammarStatus))
        {
            OnPropertyChanged(nameof(GrammarStatus));
            OnPropertyChanged(nameof(GrammarStateBrush));
        }
    }

    public void Apply()
    {
        _mainViewModel.ApplySettings(new SettingsSnapshot(
            AutoOffloadVram,
            UseCpuOnly,
            StartupEnabled,
            AutoPaste,
            MinimizeToTray,
            StartInTray,
            LocalModelId,
            HotkeyVirtualKeyCode,
            GrammarEnabled,
            GrammarApiHost,
            GrammarApiKey,
            GrammarEffort,
            SelectionToolbarEnabled,
            VisualizerEnabled,
            VisualizerStyle,
            VisualizerPalette));
    }

    static IReadOnlyList<HotkeyOption> CreateHotkeyOptions(int currentKeyCode) =>
        HotkeyCatalog.Options(currentKeyCode)
            .Select(option => new HotkeyOption(option.Vk, option.Name))
            .ToList();

    static IReadOnlyList<MicOption> CreateMicOptions(int currentDeviceNumber)
    {
        var options = new List<MicOption>
        {
            new(-1, "System default")
        };

        foreach (var (number, name) in AudioRecorder.GetInputDevices())
            options.Add(new MicOption(number, name));

        if (currentDeviceNumber >= 0 && !options.Exists(option => option.Number == currentDeviceNumber))
            options.Add(new MicOption(currentDeviceNumber, $"Device {currentDeviceNumber} (not connected)"));

        return options;
    }
}

public sealed record SettingsSnapshot(
    bool AutoOffloadVram,
    bool UseCpuOnly,
    bool StartupEnabled,
    bool AutoPaste,
    bool MinimizeToTray,
    bool StartInTray,
    string? LocalModelId,
    int HotkeyVirtualKeyCode,
    bool GrammarEnabled,
    string GrammarApiHost,
    string GrammarApiKey,
    string GrammarEffort,
    bool SelectionToolbarEnabled,
    bool VisualizerEnabled,
    string VisualizerStyle,
    string VisualizerPalette);

public sealed class HotkeyOption
{
    public int VirtualKeyCode { get; }
    public string Name { get; }

    public HotkeyOption(int virtualKeyCode, string name)
    {
        VirtualKeyCode = virtualKeyCode;
        Name = name;
    }

    public override string ToString() => Name;
}

public sealed class MicOption
{
    public int Number { get; }
    public string Name { get; }

    public MicOption(int number, string name)
    {
        Number = number;
        Name = name;
    }

    public override string ToString() => Name;
}
