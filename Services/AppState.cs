using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class AppState
{
    readonly AppSettings _settings;

    public ProviderConfig? ActiveProvider => _settings.ActiveProvider;
    public ProviderConfig? LocalProvider => _settings.Providers.Find(provider => provider.IsLocal);
    public ProviderConfig? CloudProvider => _settings.Providers.Find(provider => !provider.IsLocal);

    public string CloudLlmUrl => CloudProvider?.ApiEndpoint ?? "";
    public IReadOnlyList<string> CloudLlmUrls =>
        CloudProvider?.GetApiEndpoints() ?? System.Array.Empty<string>();

    public int ActiveProviderIndex
    {
        get => _settings.ActiveProviderIndex;
        set => _settings.ActiveProviderIndex = value;
    }

    public IReadOnlyList<ProviderConfig> Providers => _settings.Providers;
    public ObservableCollection<ProviderConfig> ProvidersObservable { get; }
    public bool AutoOffloadVram
    {
        get => _settings.AutoOffloadVram;
        set { _settings.AutoOffloadVram = value; _settings.Save(); }
    }
    public bool ThinkingEnabled
    {
        get => _settings.ThinkingEnabled;
        set { _settings.ThinkingEnabled = value; _settings.Save(); }
    }
    public bool StartupEnabled
    {
        get => _settings.StartupEnabled;
        set { _settings.StartupEnabled = value; _settings.Save(); }
    }
    public int HotkeyVirtualKeyCode
    {
        get => _settings.HotkeyVirtualKeyCode;
        set { _settings.HotkeyVirtualKeyCode = value; _settings.Save(); }
    }
    public bool HotkeyEnabled
    {
        get => _settings.HotkeyEnabled;
        set { _settings.HotkeyEnabled = value; _settings.Save(); }
    }

    public AppState(AppSettings settings)
    {
        _settings = settings;
        ProvidersObservable = new ObservableCollection<ProviderConfig>(settings.Providers);
    }

    public void SetActiveProvider(int index)
    {
        ActiveProviderIndex = index;
        _settings.Save();
    }

    public bool SetActiveProviderForMode(bool useCloud)
    {
        var provider = useCloud ? CloudProvider : LocalProvider;
        if (provider == null)
            return false;

        var index = _settings.Providers.IndexOf(provider);
        if (index == ActiveProviderIndex)
            return false;

        ActiveProviderIndex = index;
        _settings.Save();
        return true;
    }

    public bool SetCloudLlmUrls(IEnumerable<string> endpoints)
    {
        var provider = CloudProvider;
        if (provider == null)
            return false;

        var normalizedEndpoints = new List<string>();
        foreach (var endpoint in endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                continue;
            if (!AppSettings.TryNormalizeHttpEndpoint(endpoint, out var normalizedEndpoint))
                return false;
            normalizedEndpoints.Add(normalizedEndpoint);
        }

        if (normalizedEndpoints.Count == 0)
            return false;

        provider.ApiEndpoints ??= new List<string>();
        if (provider.ApiEndpoints.SequenceEqual(normalizedEndpoints) &&
            provider.ApiEndpoint == normalizedEndpoints[0])
            return false;

        provider.ApiEndpoints = normalizedEndpoints;
        provider.ApiEndpoint = normalizedEndpoints[0];
        _settings.Save();
        return true;
    }

    public void Save() => _settings.Save();
}
