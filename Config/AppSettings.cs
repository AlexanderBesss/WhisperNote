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
    public string? LocalModelId { get; set; }
    public bool AutoOffloadVram { get; set; }
    public bool UseCpuOnly { get; set; }
    public bool StartupEnabled { get; set; } = true;
    public bool AutoPaste { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartInTray { get; set; } = true;
    public int HotkeyVirtualKeyCode { get; set; } = DefaultHotkeyVkCode;
    // -1 = system default; otherwise an index into the active WASAPI capture endpoints.
    public int MicDeviceNumber { get; set; } = -1;
    // Preferred over the number: WASAPI indexes reshuffle when devices are added/removed.
    public string? MicDeviceName { get; set; }
    public bool MicDeviceNumberIsWasapi { get; set; }

    // Fixes the text selected in any app through a Ctrl+C / correct / Ctrl+V round trip,
    // triggered from the floating "Fix" popup driven by UIA text-selection events.
    public bool SelectionToolbarEnabled { get; set; } = true;

    // Live mic visualization drawn in the recording overlay while capturing.
    // The style is a display name resolved by VisualizerCatalog.Resolve, so an
    // older build reading a newer name falls back to Spectrum.
    public bool VisualizerEnabled { get; set; } = true;
    public string VisualizerStyle { get; set; } = VisualizerCatalog.DefaultStyleName;
    // Colour scheme name resolved by VisualizerPaletteCatalog.Resolve, same fallback rule.
    public string VisualizerPalette { get; set; } = VisualizerPaletteCatalog.DefaultPaletteName;
    // Overlay animation rate; VisualizerFpsCatalog.Resolve clamps unknown values to the default.
    public int VisualizerFps { get; set; } = VisualizerFpsCatalog.DefaultFps;
    // Post-recording pass animation style, a display name resolved by ProcessingCatalog.Resolve,
    // same fallback rule as the visualizer style.
    public string ProcessingAnimation { get; set; } = ProcessingCatalog.DefaultStyleName;

    public bool GrammarCheckEnabled { get; set; }
    // API root; "/v1/models" and "/v1/chat/completions" are requested under it.
    public string GrammarApiEndpoint { get; set; } = "";
    public string GrammarApiKey { get; set; } = "";
    // "minimal"|"low"|"medium"|"high", forwarded as the "reasoning_effort" chat parameter; empty = server default.
    public string GrammarReasoningEffort { get; set; } = "";

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
            var changed = settings.NormalizeProviders();
            if (!settings.MicDeviceNumberIsWasapi)
            {
                settings.MicDeviceNumber = -1;
                settings.MicDeviceNumberIsWasapi = true;
                changed = true;
            }
            if (changed)
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
            AutoOffloadVram = false,
            UseCpuOnly = false,
            StartupEnabled = true,
            AutoPaste = true,
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
            // ServerExe intentionally empty: the server binary is picked by hardware detection (iGPU > NVIDIA > NPU).
            HfRepo = option.HfRepo
        };
    }
}
