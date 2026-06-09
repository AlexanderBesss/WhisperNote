using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperNote.Services;

namespace WhisperNote.Config;

public class AppSettings
{
    public int ActiveProviderIndex { get; set; }
    public List<ProviderConfig> Providers { get; set; } = new();
    public bool AutoOffloadVram { get; set; }

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
        catch
        {
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
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Name = "Gemma 4 E2B (local)",
                    Type = "local",
                    ApiEndpoint = "http://localhost:8082",
                    Model = "gemma-4-E2B-it-Q4_K_M.gguf",
                    Mmproj = "mmproj-gemma-4-E2B-it-BF16.gguf",
                    ServerExe = @"llama\llama-server.exe"
                },
                new()
                {
                    Name = "Gemma 4 12B (local)",
                    Type = "local",
                    ApiEndpoint = "http://localhost:8082",
                    Model = "gemma-4-12B-it-QAT-Q4_0.gguf",
                    Mmproj = "mmproj-gemma-4-12B-it-QAT-BF16.gguf",
                    ServerExe = @"llama\llama-server.exe"
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
