using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperNote.Services;

namespace WhisperNote.Config;

public class AppSettings
{
    const int DefaultHotkeyVkCode = 0xA3;

    public int ActiveProviderIndex { get; set; }
    public List<ProviderConfig> Providers { get; set; } = new();
    // Null on legacy configs: resolved from the local provider's model file in NormalizeProviders.
    public string? LocalModelId { get; set; }
    public bool AutoOffloadVram { get; set; }
    public bool UseCpuOnly { get; set; }
    public bool StartupEnabled { get; set; }
    public bool AutoPaste { get; set; }
    // Initializer supplies the default for configs written before the key existed.
    public bool MinimizeToTray { get; set; } = true;
    public bool StartInTray { get; set; } = true;
    public int HotkeyVirtualKeyCode { get; set; } = DefaultHotkeyVkCode;
    public bool HotkeyEnabled { get; set; } = true;

    static string ConfigPath() => AppPaths.SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path))
            {
                var defaults = CreateDefault();
                defaults.Save();
                return defaults;
            }
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings?.Providers == null || settings.Providers.Count == 0)
            {
                var defaults = CreateDefault();
                defaults.Save();
                return defaults;
            }
            if (settings.NormalizeProviders())
                settings.Save();
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load settings: {ex.Message}");
            return CreateDefault();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath(), json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Save config: {ex.Message}");
        }
    }

    [JsonIgnore]
    public ProviderConfig? ActiveProvider =>
        Providers.Count == 0 ? null :
        ActiveProviderIndex >= 0 && ActiveProviderIndex < Providers.Count
            ? Providers[ActiveProviderIndex]
            : Providers[0];

    static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            ActiveProviderIndex = 0,
            LocalModelId = LocalModels.DefaultId,
            AutoOffloadVram = true,
            UseCpuOnly = false,
            StartupEnabled = false,
            AutoPaste = false,
            MinimizeToTray = true,
            Providers = new List<ProviderConfig>
            {
                CreateDefaultLocalProvider()
            }
        };
    }

    bool NormalizeProviders()
    {
        var changed = false;

        // Configs written while the removed remote-provider feature existed
        // may still list cloud/remote-execution entries; the app is local-only
        // now, so drop them.
        if (Providers.RemoveAll(provider => !provider.IsLocal) > 0)
            changed = true;

        if (!Providers.Exists(provider => provider.IsLocal))
        {
            Providers.Insert(0, CreateDefaultLocalProvider());
            changed = true;
        }

        var local = Providers.Find(p => p.IsLocal);
        if (local != null)
        {
            // Keep the local provider in sync with the selected model. Unknown
            // selections and custom models that match no catalog entry are left alone.
            var option = LocalModels.Resolve(LocalModelId, local.Model);
            if (option != null &&
                (LocalModelId != option.Id ||
                 local.Name != option.ProviderName ||
                 local.Model != option.Model ||
                 local.HfRepo != option.HfRepo ||
                 local.Mmproj != option.Mmproj))
            {
                LocalModelId = option.Id;
                local.Name = option.ProviderName;
                local.Model = option.Model;
                local.HfRepo = option.HfRepo;
                local.Mmproj = option.Mmproj;
                changed = true;
            }
        }

        if (ActiveProviderIndex < 0 || ActiveProviderIndex >= Providers.Count)
        {
            ActiveProviderIndex = 0;
            changed = true;
        }

        return changed;
    }

    static ProviderConfig CreateDefaultLocalProvider()
    {
        var option = LocalModels.FindById(LocalModels.DefaultId)!;
        return new ProviderConfig
        {
            Name = option.ProviderName,
            Type = "local",
            ApiEndpoint = "http://localhost:8082",
            Model = option.Model,
            Mmproj = option.Mmproj,
            // ServerExe intentionally left empty: the server binary is picked by
            // hardware detection (iGPU > NVIDIA > NPU). Set it explicitly to override.
            HfRepo = option.HfRepo
        };
    }
}
