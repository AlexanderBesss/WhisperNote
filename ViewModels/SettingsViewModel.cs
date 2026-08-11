using System.Collections.Generic;
using WhisperNote.Config;

namespace WhisperNote.ViewModels;

public sealed class SettingsViewModel : ViewModel
{
    readonly MainWindowViewModel _mainViewModel;
    string _cloudLlmUrl = "";
    bool _autoOffloadVram;
    bool _thinkingEnabled;
    bool _startupEnabled;
    bool _useRemote;
    bool _hotkeyEnabled;
    int _hotkeyVirtualKeyCode;

    public bool AutoOffloadVram
    {
        get => _autoOffloadVram;
        set => SetProperty(ref _autoOffloadVram, value);
    }

    public bool ThinkingEnabled
    {
        get => _thinkingEnabled;
        set => SetProperty(ref _thinkingEnabled, value);
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set => SetProperty(ref _startupEnabled, value);
    }

    public bool UseRemote
    {
        get => _useRemote;
        set => SetProperty(ref _useRemote, value);
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

    public string CloudLlmUrl
    {
        get => _cloudLlmUrl;
        set
        {
            if (!SetProperty(ref _cloudLlmUrl, value ?? ""))
                return;

            OnPropertyChanged(nameof(IsCloudLlmUrlValid));
            OnPropertyChanged(nameof(CloudLlmUrlValidationMessage));
        }
    }

    public bool IsCloudLlmUrlValid => AppSettings.TryNormalizeHttpEndpoint(CloudLlmUrl, out _);

    public string CloudLlmUrlValidationMessage => IsCloudLlmUrlValid
        ? ""
        : "Enter a valid HTTP or HTTPS URL.";

    public IReadOnlyList<HotkeyOption> HotkeyOptions { get; }

    public SettingsViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _autoOffloadVram = mainViewModel.AutoOffloadVram;
        _thinkingEnabled = mainViewModel.ThinkingEnabled;
        _startupEnabled = mainViewModel.StartupEnabled;
        _useRemote = mainViewModel.UseRemote;
        _hotkeyEnabled = mainViewModel.HotkeyEnabled;
        _hotkeyVirtualKeyCode = mainViewModel.HotkeyVirtualKeyCode;
        _cloudLlmUrl = mainViewModel.CloudLlmUrl;
        HotkeyOptions = CreateHotkeyOptions(_hotkeyVirtualKeyCode);
    }

    public bool TryApply()
    {
        if (!AppSettings.TryNormalizeHttpEndpoint(CloudLlmUrl, out var normalizedCloudLlmUrl))
            return false;

        _mainViewModel.ApplySettings(
            AutoOffloadVram,
            ThinkingEnabled,
            StartupEnabled,
            UseRemote,
            HotkeyEnabled,
            HotkeyVirtualKeyCode,
            normalizedCloudLlmUrl);
        return true;
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
}
