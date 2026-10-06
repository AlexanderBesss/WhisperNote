using System;
using System.IO;
using WhisperNote;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

/// <summary>
/// Covers the multi-backend startup path: which binary each detected GPU family runs,
/// the order the fallback chain tries them in, and which failures count as
/// "this device cannot run this backend".
/// </summary>
public class BackendSelectionTests : IDisposable
{
    public BackendSelectionTests() => App.DetectedBackend = HardwareBackend.Unknown;

    public void Dispose() => App.DetectedBackend = HardwareBackend.Unknown;

    [Theory]
    [InlineData(HardwareBackend.NvidiaCuda, @"llama\llama-server.exe")]
    [InlineData(HardwareBackend.NvidiaCudaLegacy, @"cuda12\llama-server.exe")]
    [InlineData(HardwareBackend.Vulkan, @"vulkan\llama-server.exe")]
    [InlineData(HardwareBackend.IntelNpu, @"NPU\llama-ov\llama-server.exe")]
    [InlineData(HardwareBackend.Cpu, @"llama\llama-server.exe")]
    public void EachBackendResolvesToItsOwnServerBinary(HardwareBackend backend, string expectedRelative)
    {
        var server = CreateConfiguredServer(backend);

        var exe = server.ResolveServerExe(backend);

        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, expectedRelative).Replace('\\', '/'),
            exe.Replace('\\', '/'));
    }

    [Fact]
    public void StaleDefaultPinIsIgnoredOnNonCuda13Backends()
    {
        // Settings written by older versions always pinned the CUDA 13 binary. On a
        // Pascal machine that pin must not stop the legacy backend from picking up
        // the fast cuda12\ binary.
        App.DetectedBackend = HardwareBackend.NvidiaCudaLegacy;
        var server = new LlmServer();
        server.Configure(new ProviderConfig
        {
            Name = "Local",
            Type = "local",
            ApiEndpoint = "http://localhost:8082",
            Model = "model.gguf",
            ServerExe = @"llama\llama-server.exe"
        });

        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, @"cuda12\llama-server.exe").Replace('\\', '/'),
            server.ResolveServerExe(HardwareBackend.NvidiaCudaLegacy).Replace('\\', '/'));
    }

    [Fact]
    public void CustomPinAppliesOnlyToTheDetectedBackend()
    {
        // A deliberate custom exe is honoured for the detected backend only; the other
        // backends in the fallback chain keep their own binaries, so the pin cannot
        // block the switch when the pinned exe fails on this GPU.
        App.DetectedBackend = HardwareBackend.NvidiaCudaLegacy;
        var server = new LlmServer();
        server.Configure(new ProviderConfig
        {
            Name = "Local",
            Type = "local",
            ApiEndpoint = "http://localhost:8082",
            Model = "model.gguf",
            ServerExe = @"custom\llama-server.exe"
        });

        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, @"custom\llama-server.exe").Replace('\\', '/'),
            server.ResolveServerExe(HardwareBackend.NvidiaCudaLegacy).Replace('\\', '/'));
        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, @"vulkan\llama-server.exe").Replace('\\', '/'),
            server.ResolveServerExe(HardwareBackend.Vulkan).Replace('\\', '/'));
    }

    [Fact]
    public void ProviderServerExePinIsHonouredForTheDetectedBackend()
    {
        App.DetectedBackend = HardwareBackend.NvidiaCuda;
        var server = new LlmServer();
        server.Configure(new ProviderConfig
        {
            Name = "Local",
            Type = "local",
            ApiEndpoint = "http://localhost:8082",
            Model = "model.gguf",
            ServerExe = @"custom\llama-server.exe"
        });

        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, @"custom\llama-server.exe").Replace('\\', '/'),
            server.ResolveServerExe(HardwareBackend.NvidiaCuda).Replace('\\', '/'));
    }

    [Fact]
    public void PascalChainNeverTriesTheCuda13Build()
    {
        var server = CreateConfiguredServer(HardwareBackend.NvidiaCudaLegacy);

        var chain = server.FallbackChain();

        Assert.Equal(
            new[] { HardwareBackend.NvidiaCudaLegacy, HardwareBackend.Vulkan, HardwareBackend.Cpu },
            chain);
    }

    [Fact]
    public void ModernNvidiaChainKeepsCuda13FirstAndDegradesThroughLegacy()
    {
        var server = CreateConfiguredServer(HardwareBackend.NvidiaCuda);

        Assert.Equal(
            new[]
            {
                HardwareBackend.NvidiaCuda, HardwareBackend.NvidiaCudaLegacy,
                HardwareBackend.Vulkan, HardwareBackend.Cpu
            },
            server.FallbackChain());
    }

    [Theory]
    [InlineData(HardwareBackend.Vulkan)]
    [InlineData(HardwareBackend.IntelNpu)]
    public void SingleBackendChainsEndOnCpu(HardwareBackend backend)
    {
        var server = CreateConfiguredServer(backend);

        var chain = server.FallbackChain();

        Assert.Equal(backend, chain[0]);
        Assert.Equal(HardwareBackend.Cpu, chain[^1]);
    }

    [Fact]
    public void CpuOnlyModeSkipsEveryGpuBackend()
    {
        var server = CreateConfiguredServer(HardwareBackend.NvidiaCudaLegacy);
        server.SetUseCpuOnly(true);

        Assert.Equal(new[] { HardwareBackend.Cpu }, server.FallbackChain());
    }

    [Fact]
    public void DetectedCpuBackendRunsServerArgsWithoutGpuFlags()
    {
        // The fallback chain lands on Cpu without the user toggling CPU-only mode;
        // the args must still avoid the GPU paths.
        var server = CreateConfiguredServer(HardwareBackend.Cpu);

        var args = server.ServerArgs();

        Assert.Contains("--device none", args);
        Assert.Contains("--gpu-layers 0", args);
        Assert.DoesNotContain("--flash-attn on", args);
    }

    [Theory]
    [InlineData("ggml_cuda_init: failed with error 101: no kernel image is available for execution on the device")]
    [InlineData("0 [0] CUDA error: CUDA driver version is insufficient for CUDA runtime version")]
    [InlineData("ggml_backend_vk: no Vulkan devices found")]
    [InlineData("vkCreateInstance failed: VK_ERROR_INITIALIZATION_FAILED")]
    public void DeviceIncompatibilityOutputTriggersFallback(string output)
    {
        Assert.True(LlmServer.IsUnsupportedDeviceError(output));
    }

    [Theory]
    [InlineData("main: server is listening on 127.0.0.1:8082")]
    [InlineData("load_backend: loaded CUDA backend")]
    [InlineData("print_info: model = Qwen3-ASR-1.7B")]
    public void OrdinaryServerOutputDoesNotTriggerFallback(string output)
    {
        Assert.False(LlmServer.IsUnsupportedDeviceError(output));
    }

    static LlmServer CreateConfiguredServer(HardwareBackend detected)
    {
        App.DetectedBackend = detected;
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
