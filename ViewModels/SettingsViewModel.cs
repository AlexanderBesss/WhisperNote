using System.Collections.Generic;
using WhisperNote.Config;

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
    }

    public void Apply()
    {
        _mainViewModel.ApplySettings(
            AutoOffloadVram,
            UseCpuOnly,
            StartupEnabled,
            AutoPaste,
            MinimizeToTray,
            StartInTray,
            LocalModelId,
            HotkeyEnabled,
            HotkeyVirtualKeyCode);
    }

    static IReadOnlyList<HotkeyOption> CreateHotkeyOptions(int currentKeyCode)
    {
        var options = new List<HotkeyOption>
        {
            new(0xA3, "Right Ctrl"),
            new(0xA5, "Right Alt"),
            new(0x14, "Caps Lock"),
            new(0xA0, "Left Shift"),
            new(0xA1, "Right Shift"),
            new(0x10, "Ctrl"),
            new(0x11, "Alt"),
            new(0x5B, "Left Win"),
            new(0x5C, "Right Win")
        };

        if (!options.Exists(option => option.VirtualKeyCode == currentKeyCode))
            options.Add(new HotkeyOption(currentKeyCode, MainWindowViewModel.VkCodeToString(currentKeyCode)));

        return options;
    }
}

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
