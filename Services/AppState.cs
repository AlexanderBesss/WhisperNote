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
