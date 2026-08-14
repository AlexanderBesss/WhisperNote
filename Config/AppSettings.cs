using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperNote.Services;

namespace WhisperNote.Config;

public class AppSettings
{
    const int DefaultHotkeyVkCode = 0xA3;
    public const string DefaultCloudLlmUrl = "http://192.168.0.96:8082";

    public int ActiveProviderIndex { get; set; }
    public List<ProviderConfig> Providers { get; set; } = new();
    public bool AutoOffloadVram { get; set; }
    public bool ThinkingEnabled { get; set; }
    public bool StartupEnabled { get; set; }
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
            StartupEnabled = false,
            Providers = new List<ProviderConfig>
            {
                CreateDefaultLocalProvider(),
                CreateDefaultRemoteProvider()
            }
        };
    }

    bool NormalizeProviders()
    {
        var changed = false;
        var hasLocal = Providers.Exists(p => p.IsLocal);
        var hasRemote = Providers.Exists(p => !p.IsLocal);

        if (!hasLocal)
        {
            Providers.Insert(0, CreateDefaultLocalProvider());
            changed = true;
        }

        if (!hasRemote)
        {
            Providers.Add(CreateDefaultRemoteProvider());
            changed = true;
        }

        foreach (var provider in Providers)
        {
            if (provider.IsLocal)
                continue;

            provider.ApiEndpoints ??= new List<string>();
            var configuredEndpoints = provider.ApiEndpoints.Count > 0
                ? provider.ApiEndpoints
                : new List<string> { provider.ApiEndpoint };
            var normalizedEndpoints = new List<string>();
            foreach (var endpoint in configuredEndpoints)
            {
                if (TryNormalizeHttpEndpoint(endpoint, out var normalizedEndpoint))
                    normalizedEndpoints.Add(normalizedEndpoint);
            }

            if (normalizedEndpoints.Count == 0)
                normalizedEndpoints.Add(DefaultCloudLlmUrl);

            if (!provider.ApiEndpoints.SequenceEqual(normalizedEndpoints))
            {
                provider.ApiEndpoints = normalizedEndpoints;
                changed = true;
            }

            if (provider.ApiEndpoint != normalizedEndpoints[0])
            {
                provider.ApiEndpoint = normalizedEndpoints[0];
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

    public static bool TryNormalizeHttpEndpoint(string? endpoint, out string normalizedEndpoint)
    {
        normalizedEndpoint = endpoint?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var uri))
            return false;

        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            normalizedEndpoint = "";
            return false;
        }

        return true;
    }

    static ProviderConfig CreateDefaultLocalProvider() => new()
    {
        Name = "Gemma 4 E2B UD (local)",
        Type = "local",
        ApiEndpoint = "http://localhost:8082",
        Model = "gemma-4-E2B-it-Q4_0.gguf",
        Mmproj = "mmproj-BF16.gguf",
        ServerExe = @"llama\llama-server.exe",
        HfRepo = "unsloth/gemma-4-E2B-it-GGUF"
    };

    static ProviderConfig CreateDefaultRemoteProvider() => new()
    {
        Name = "Remote (192.168.0.96)",
        Type = "remote",
        ApiEndpoint = DefaultCloudLlmUrl,
        ApiEndpoints = new List<string> { DefaultCloudLlmUrl },
        Model = "gemma-4-E2B-it-Q4_0.gguf"
    };
}
