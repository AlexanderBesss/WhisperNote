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
    bool _hotkeyEnabled;
    int _hotkeyVirtualKeyCode;
    int _micDeviceNumber;
    bool _grammarEnabled;
    string _grammarApiHost = "";
    string _grammarApiKey = "";
    string _grammarEffort = "";

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

    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set => SetProperty(ref _hotkeyEnabled, value);
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

            // Apply right away: the mic choice takes effect on the next
            // recording and must be persisted the moment it changes.
            // "System default" (-1) has no name to remember.
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

    // The host and key deliberately commit when the field loses focus (WPF's
    // default TextBox trigger): every keystroke would otherwise persist the
    // config and re-probe the endpoint.
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

    // Which thinking effort to ask the server for. The list comes from the
    // service itself, so the dropdown and the request cannot drift apart.
    public string GrammarEffort
    {
        get => _grammarEffort;
        set => SetProperty(ref _grammarEffort, value);
    }

    public IReadOnlyList<GrammarEffortOption> GrammarEffortOptions { get; } = GrammarCheckService.EffortOptions;

    // Mirrors what the main view model learned from the API: which model it
    // reported, or why the pass is being skipped.
    public string GrammarStatus => _mainViewModel.GrammarStatus;

    // The same state as that sentence, as the colour of the dot the tab shows
    // next to the switch instead of a status line.
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
        _hotkeyEnabled = mainViewModel.HotkeyEnabled;
        _hotkeyVirtualKeyCode = mainViewModel.HotkeyVirtualKeyCode;
        HotkeyOptions = CreateHotkeyOptions(_hotkeyVirtualKeyCode);
        _micDeviceNumber = mainViewModel.MicDeviceNumber;
        MicOptions = CreateMicOptions(_micDeviceNumber);
        _grammarEnabled = mainViewModel.GrammarEnabled;
        _grammarApiHost = mainViewModel.GrammarApiHost;
        _grammarApiKey = mainViewModel.GrammarApiKey;
        _grammarEffort = mainViewModel.GrammarEffort;

        // The probe runs on its own (at startup, and whenever the host or key
        // change), so the tab has to follow the main view model's status line.
        mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

        // Save on change: every edit is pushed to the main view model right
        // away, which persists it. The constructor filled the backing fields
        // directly, so no event fires until the user actually changes something.
        // The mic selection applies itself in its setter; re-applying the other
        // values on top of it is harmless because the main setters are no-ops
        // when the value did not change.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MicDeviceNumber))
                Apply();
        };
    }

    // The settings window is transient while the main view model lives for the
    // whole session: drop the mirror subscription when the window closes so it
    // is not kept alive.
    public void Detach() =>
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;

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
            HotkeyEnabled,
            HotkeyVirtualKeyCode,
            GrammarEnabled,
            GrammarApiHost,
            GrammarApiKey,
            GrammarEffort));
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

        // A saved device that is currently unplugged still needs a row, so the
        // combo box keeps the user's choice instead of silently resetting.
        if (currentDeviceNumber >= 0 && !options.Exists(option => option.Number == currentDeviceNumber))
            options.Add(new MicOption(currentDeviceNumber, $"Device {currentDeviceNumber} (not connected)"));

        return options;
    }
}

// Everything the settings window can change, handed to the main view model as
// one unit: nine positional arguments were easy to transpose silently.
public sealed record SettingsSnapshot(
    bool AutoOffloadVram,
    bool UseCpuOnly,
    bool StartupEnabled,
    bool AutoPaste,
    bool MinimizeToTray,
    bool StartInTray,
    string? LocalModelId,
    bool HotkeyEnabled,
    int HotkeyVirtualKeyCode,
    bool GrammarEnabled,
    string GrammarApiHost,
    string GrammarApiKey,
    string GrammarEffort);

public sealed class HotkeyOption
{
    public int VirtualKeyCode { get; }
    public string Name { get; }

    public HotkeyOption(int virtualKeyCode, string name)
    {
        VirtualKeyCode = virtualKeyCode;
        Name = name;
    }

    // The custom ComboBox template shows the raw item, so ToString() is what
    // ends up in the selection box and the dropdown rows.
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
