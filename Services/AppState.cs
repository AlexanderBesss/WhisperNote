using System.Collections.Generic;
using WhisperNote.Config;

namespace WhisperNote.Services;

public class AppState
{
    readonly AppSettings _settings;

    public ProviderConfig? ActiveProvider => _settings.ActiveProvider;
    public ProviderConfig? LocalProvider => _settings.Providers.Find(provider => provider.IsLocal);

    public int ActiveProviderIndex
    {
        get => _settings.ActiveProviderIndex;
        set => _settings.ActiveProviderIndex = value;
    }

    public IReadOnlyList<ProviderConfig> Providers => _settings.Providers;
    public string? LocalModelId
    {
        get => _settings.LocalModelId;
        set { _settings.LocalModelId = value; _settings.Save(); }
    }

    public bool AutoOffloadVram
    {
        get => _settings.AutoOffloadVram;
        set { _settings.AutoOffloadVram = value; _settings.Save(); }
    }
    public bool UseCpuOnly
    {
        get => _settings.UseCpuOnly;
        set { _settings.UseCpuOnly = value; _settings.Save(); }
    }

    public bool StartupEnabled
    {
        get => _settings.StartupEnabled;
        set { _settings.StartupEnabled = value; _settings.Save(); }
    }
    public bool AutoPaste
    {
        get => _settings.AutoPaste;
        set { _settings.AutoPaste = value; _settings.Save(); }
    }
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { _settings.MinimizeToTray = value; _settings.Save(); }
    }
    public bool StartInTray
    {
        get => _settings.StartInTray;
        set { _settings.StartInTray = value; _settings.Save(); }
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
    }

    public bool SetLocalModel(string? modelId)
    {
        var option = LocalModels.FindById(modelId);
        var local = LocalProvider;
        if (option == null || local == null)
            return false;

        var changed = _settings.LocalModelId != option.Id ||
            local.Name != option.ProviderName ||
            local.Model != option.Model ||
            local.HfRepo != option.HfRepo ||
            local.Mmproj != option.Mmproj;
        if (!changed)
            return false;

        _settings.LocalModelId = option.Id;
        local.Name = option.ProviderName;
        local.Model = option.Model;
        local.HfRepo = option.HfRepo;
        local.Mmproj = option.Mmproj;
        _settings.Save();
        return true;
    }

    public void Save() => _settings.Save();
}
