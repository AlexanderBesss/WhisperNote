using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperNote.Services;

namespace WhisperNote.Config;

public class AppSettings
{
    const int DefaultHotkeyVkCode = 0xA0;

    public int ActiveProviderIndex { get; set; }
    public List<ProviderConfig> Providers { get; set; } = new();
    public bool AutoOffloadVram { get; set; }
    public bool ThinkingEnabled { get; set; }
    public int HotkeyVirtualKeyCode { get; set; } = DefaultHotkeyVkCode;
    public bool HotkeyEnabled { get; set; } = true;

    static string ConfigPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whispernote.json");

    public static AppSettings Load()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path))
                return CreateDefault();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings?.Providers == null || settings.Providers.Count == 0)
                return CreateDefault();
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
                new()
                {
                    Name = "Gemma 4 E2B UD (local)",
                    Type = "local",
                    ApiEndpoint = "http://localhost:8082",
                    Model = "gemma-4-E2B-it-UD-Q4_K_XL.gguf",
                    Mmproj = "mmproj-BF16.gguf",
                    ServerExe = @"llama\llama-server.exe",
                    HfRepo = "unsloth/gemma-4-E2B-it-GGUF"
                },
                new()
                {
                    Name = "Whisper-1 (OpenAI)",
                    Type = "openai",
                    ApiEndpoint = "https://api.openai.com/v1",
                    ApiKey = "",
                    Model = "whisper-1"
                }
            }
        };
    }
}
