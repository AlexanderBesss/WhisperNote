using System.Text.Json;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class CpuModeTests
{
    [Fact]
    public void UseCpuOnlyDefaultsToFalseForLegacySettings()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.UseCpuOnly);

        var legacy = JsonSerializer.Deserialize<AppSettings>("""
            {
              "ActiveProviderIndex": 0,
              "Providers": [
                { "Name": "Local", "Type": "local", "ApiEndpoint": "http://localhost:8082" }
              ]
            }
            """)!;

        Assert.False(legacy.UseCpuOnly);
    }

    [Fact]
    public void CpuModeServerArgsRunAllLayersOnCpu()
    {
        var server = CreateConfiguredServer();
        server.SetUseCpuOnly(true);

        var args = server.ServerArgs();

        Assert.Contains("--gpu-layers 0", args);
        Assert.DoesNotContain("--flash-attn on", args);
        Assert.DoesNotContain("--mmproj-offload", args);
        // Quantized V cache requires flash attention, which CPU mode must not use.
        Assert.DoesNotContain("--cache-type-v q4_0", args);
    }

    [Fact]
    public void DefaultServerArgsOffloadModelToGpu()
    {
        var server = CreateConfiguredServer();

        var args = server.ServerArgs();

        Assert.Contains($"--gpu-layers {AppConfig.GpuLayers}", args);
        Assert.Contains("--flash-attn on", args);
    }

    static LlmServer CreateConfiguredServer()
    {
        var server = new LlmServer();
        server.Configure(new ProviderConfig
        {
            Name = "Local",
            Type = "local",
            ApiEndpoint = "http://localhost:8082",
            Model = "model.gguf"
        });
        return server;
    }
}
