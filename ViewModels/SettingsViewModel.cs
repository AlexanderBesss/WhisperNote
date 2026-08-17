using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using WhisperNote.Config;

namespace WhisperNote.ViewModels;

public sealed class SettingsViewModel : ViewModel
{
    readonly MainWindowViewModel _mainViewModel;
    bool _autoOffloadVram;
    bool _thinkingEnabled;
    bool _startupEnabled;
    bool _useRemote;
    bool _hotkeyEnabled;
    int _hotkeyVirtualKeyCode;
    RemoteProviderMode _remoteProviderMode;
    string _remoteServerEndpoint;
    bool _remoteServerEnabled;
    string _remoteListenEndpoint;

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

    public IReadOnlyList<RemoteProviderMode> RemoteProviderModes { get; } =
        new[] { RemoteProviderMode.DirectApi, RemoteProviderMode.RemoteExecution };
    public RemoteProviderMode RemoteProviderMode
    {
        get => _remoteProviderMode;
        set
        {
            if (SetProperty(ref _remoteProviderMode, value))
            {
                OnPropertyChanged(nameof(IsDirectApiMode));
                OnPropertyChanged(nameof(IsRemoteExecutionMode));
                OnPropertyChanged(nameof(AreSettingsValid));
            }
        }
    }
    public bool IsDirectApiMode => RemoteProviderMode == RemoteProviderMode.DirectApi;
    public bool IsRemoteExecutionMode => RemoteProviderMode == RemoteProviderMode.RemoteExecution;
    public string RemoteServerEndpoint
    {
        get => _remoteServerEndpoint;
        set { if (SetProperty(ref _remoteServerEndpoint, value ?? "")) OnPropertyChanged(nameof(AreSettingsValid)); }
    }
    public bool RemoteServerEnabled
    {
        get => _remoteServerEnabled;
        set { if (SetProperty(ref _remoteServerEnabled, value)) OnPropertyChanged(nameof(AreSettingsValid)); }
    }
    public string RemoteListenEndpoint
    {
        get => _remoteListenEndpoint;
        set { if (SetProperty(ref _remoteListenEndpoint, value ?? "")) OnPropertyChanged(nameof(AreSettingsValid)); }
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

    public ObservableCollection<CloudEndpointEntry> CloudEndpoints { get; } = new();
    public bool AreCloudEndpointsValid => CloudEndpoints.Count > 0 &&
        CloudEndpoints.All(endpoint => endpoint.IsValid);
    public bool AreSettingsValid => (!IsDirectApiMode || AreCloudEndpointsValid) &&
        (!IsRemoteExecutionMode || AppSettings.TryNormalizeHttpEndpoint(RemoteServerEndpoint, out _)) &&
        (!RemoteServerEnabled || AppSettings.TryNormalizeHttpListenEndpoint(RemoteListenEndpoint, out _));
    public ICommand AddCloudEndpointCommand { get; }
    public ICommand RemoveCloudEndpointCommand { get; }

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
        _remoteProviderMode = mainViewModel.RemoteProviderMode;
        _remoteServerEndpoint = mainViewModel.RemoteServerEndpoint;
        _remoteServerEnabled = mainViewModel.RemoteServerEnabled;
        _remoteListenEndpoint = mainViewModel.RemoteListenEndpoint;
        var endpoints = mainViewModel.CloudLlmUrls.Count > 0
            ? mainViewModel.CloudLlmUrls
            : new[] { mainViewModel.CloudLlmUrl };
        foreach (var endpoint in endpoints)
            AddEndpoint(endpoint);
        if (CloudEndpoints.Count == 0)
            AddEndpoint("");

        AddCloudEndpointCommand = new RelayCommand(_ => AddEndpoint(""));
        RemoveCloudEndpointCommand = new RelayCommand(
            endpoint => RemoveEndpoint(endpoint as CloudEndpointEntry),
            endpoint => endpoint is CloudEndpointEntry entry && !entry.IsPrimary);
        HotkeyOptions = CreateHotkeyOptions(_hotkeyVirtualKeyCode);
    }

    public bool TryApply()
    {
        if (!AreSettingsValid)
            return false;

        var normalizedEndpoints = CloudEndpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Url))
            .Select(endpoint =>
            {
                AppSettings.TryNormalizeHttpEndpoint(endpoint.Url, out var normalized);
                return normalized;
            })
            .ToList();

        _mainViewModel.ApplySettings(
            AutoOffloadVram,
            ThinkingEnabled,
            StartupEnabled,
            UseRemote,
            HotkeyEnabled,
            HotkeyVirtualKeyCode,
            normalizedEndpoints,
            RemoteProviderMode,
            RemoteServerEndpoint,
            RemoteServerEnabled,
            RemoteListenEndpoint);
        return true;
    }

    void AddEndpoint(string url)
    {
        var endpoint = new CloudEndpointEntry(url, CloudEndpoints.Count);
        endpoint.PropertyChanged += Endpoint_PropertyChanged;
        CloudEndpoints.Add(endpoint);
        OnPropertyChanged(nameof(AreCloudEndpointsValid));
        OnPropertyChanged(nameof(AreSettingsValid));
    }

    void RemoveEndpoint(CloudEndpointEntry? endpoint)
    {
        if (endpoint == null || endpoint.IsPrimary)
            return;

        endpoint.PropertyChanged -= Endpoint_PropertyChanged;
        CloudEndpoints.Remove(endpoint);
        for (var index = 0; index < CloudEndpoints.Count; index++)
            CloudEndpoints[index].SetIndex(index);
        OnPropertyChanged(nameof(AreCloudEndpointsValid));
        OnPropertyChanged(nameof(AreSettingsValid));
    }

    void Endpoint_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudEndpointEntry.Url))
        {
            OnPropertyChanged(nameof(AreCloudEndpointsValid));
            OnPropertyChanged(nameof(AreSettingsValid));
        }
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

public sealed class CloudEndpointEntry : ViewModel
{
    string _url;
    int _index;

    public string Url
    {
        get => _url;
        set
        {
            if (!SetProperty(ref _url, value ?? ""))
                return;
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(ValidationMessage));
        }
    }

    public bool IsPrimary => _index == 0;
    public string DisplayName => IsPrimary ? "Primary endpoint" : $"Backup endpoint {_index}";
    public bool IsValid => IsPrimary
        ? AppSettings.TryNormalizeHttpEndpoint(Url, out _)
        : string.IsNullOrWhiteSpace(Url) || AppSettings.TryNormalizeHttpEndpoint(Url, out _);
    public string ValidationMessage => IsValid ? "" : "Enter a valid HTTP or HTTPS URL.";

    public CloudEndpointEntry(string url, int index)
    {
        _url = url;
        _index = index;
    }

    public void SetIndex(int index)
    {
        if (_index == index)
            return;
        _index = index;
        OnPropertyChanged(nameof(IsPrimary));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
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
