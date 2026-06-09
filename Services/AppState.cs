using System.Collections.Generic;
using System.Collections.ObjectModel;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class AppState
{
    readonly AppSettings _settings;

    public ProviderConfig? ActiveProvider => _settings.ActiveProvider;
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

    public void Save() => _settings.Save();
}
