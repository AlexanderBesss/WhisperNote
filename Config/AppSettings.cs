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
    public bool AutoOffloadVram { get; set; }
    public bool ThinkingEnabled { get; set; }
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
            AutoOffloadVram = true,
            ThinkingEnabled = true,
            Providers = new List<ProviderConfig>
            {
                CreateDefaultLocalProvider()
            }
        };
    }

    bool NormalizeProviders()
    {
        var localProviders = Providers.FindAll(provider => provider.IsLocal);
        var changed = localProviders.Count != Providers.Count;

        if (localProviders.Count == 0)
        {
            localProviders.Add(CreateDefaultLocalProvider());
            changed = true;
        }

        if (ActiveProviderIndex < 0 ||
            ActiveProviderIndex >= localProviders.Count ||
            !Providers[ActiveProviderIndex].IsLocal)
        {
            ActiveProviderIndex = 0;
            changed = true;
        }

        if (changed)
            Providers = localProviders;

        return changed;
    }

    static ProviderConfig CreateDefaultLocalProvider() => new()
    {
        Name = "Gemma 4 E2B UD (local)",
        Type = "local",
        ApiEndpoint = "http://localhost:8082",
        Model = "gemma-4-E2B-it-UD-Q4_K_XL.gguf",
        Mmproj = "mmproj-BF16.gguf",
        ServerExe = @"llama\llama-server.exe",
        HfRepo = "unsloth/gemma-4-E2B-it-GGUF"
    };
}
