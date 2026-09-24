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
    public const string DefaultRemoteExecutionUrl = "http://localhost:8090";
    public const string DefaultRemoteListenEndpoint = "http://0.0.0.0:8090";

    public int ActiveProviderIndex { get; set; }
    public List<ProviderConfig> Providers { get; set; } = new();
    // Null on legacy configs: resolved from the local provider's model file in NormalizeProviders.
    public string? LocalModelId { get; set; }
    public bool AutoOffloadVram { get; set; }
    public bool ThinkingEnabled { get; set; }
    public bool UseCpuOnly { get; set; }
    public bool StartupEnabled { get; set; }
    public bool AutoPaste { get; set; }
    public int HotkeyVirtualKeyCode { get; set; } = DefaultHotkeyVkCode;
    public bool HotkeyEnabled { get; set; } = true;
    public RemoteProviderMode RemoteProviderMode { get; set; } = RemoteProviderMode.DirectApi;
    public string RemoteServerEndpoint { get; set; } = DefaultRemoteExecutionUrl;
    public bool RemoteServerEnabled { get; set; }
    public bool RemoteSettingsControlEnabled { get; set; }
    public string RemoteListenEndpoint { get; set; } = DefaultRemoteListenEndpoint;

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
            ThinkingEnabled = true,
            UseCpuOnly = false,
            StartupEnabled = false,
            AutoPaste = false,
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
        if (!Enum.IsDefined(RemoteProviderMode))
        {
            RemoteProviderMode = RemoteProviderMode.DirectApi;
            changed = true;
        }

        if (!TryNormalizeHttpEndpoint(RemoteServerEndpoint, out var remoteServerEndpoint))
            remoteServerEndpoint = DefaultRemoteExecutionUrl;
        if (RemoteServerEndpoint != remoteServerEndpoint)
        {
            RemoteServerEndpoint = remoteServerEndpoint;
            changed = true;
        }

        if (!TryNormalizeHttpListenEndpoint(RemoteListenEndpoint, out var remoteListenEndpoint))
            remoteListenEndpoint = DefaultRemoteListenEndpoint;
        if (RemoteListenEndpoint != remoteListenEndpoint)
        {
            RemoteListenEndpoint = remoteListenEndpoint;
            changed = true;
        }
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

    public static bool TryNormalizeHttpListenEndpoint(string? endpoint, out string normalizedEndpoint)
    {
        if (!TryNormalizeHttpEndpoint(endpoint, out normalizedEndpoint) ||
            !Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp)
        {
            normalizedEndpoint = "";
            return false;
        }
        return true;
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

    static ProviderConfig CreateDefaultRemoteProvider() => new()
    {
        Name = "Remote (192.168.0.96)",
        Type = "remote",
        ApiEndpoint = DefaultCloudLlmUrl,
        ApiEndpoints = new List<string> { DefaultCloudLlmUrl },
        Model = "gemma-4-E2B-it-Q4_0.gguf"
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteProviderMode
{
    DirectApi,
    RemoteExecution
}
