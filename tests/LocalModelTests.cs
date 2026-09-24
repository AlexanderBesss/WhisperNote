using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class LocalModelTests
{
    [Fact]
    public void CatalogContainsGemmaAndBothQwen3AsrModels()
    {
        Assert.Equal(3, LocalModels.All.Count);

        var gemma = LocalModels.FindById(LocalModels.DefaultId);
        Assert.NotNull(gemma);
        Assert.Equal("gemma-4-E2B-it-Q4_0.gguf", gemma!.Model);
        Assert.Equal("unsloth/gemma-4-E2B-it-GGUF", gemma.HfRepo);

        var qwen06 = LocalModels.FindById("qwen3-asr-0.6b");
        Assert.NotNull(qwen06);
        Assert.Equal("Qwen3-ASR-0.6B-Q8_0.gguf", qwen06!.Model);
        Assert.Equal("unslothai/Qwen3-ASR-0.6B-GGUF", qwen06.HfRepo);
        Assert.Equal("mmproj-Qwen3-ASR-0.6B-Q8_0.gguf", qwen06.Mmproj);

        var qwen17 = LocalModels.FindById("qwen3-asr-1.7b");
        Assert.NotNull(qwen17);
        Assert.Equal("Qwen3-ASR-1.7B-Q8_0.gguf", qwen17!.Model);
        Assert.Equal("unslothai/Qwen3-ASR-1.7B-GGUF", qwen17.HfRepo);
        Assert.Equal("mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", qwen17.Mmproj);

        Assert.False(gemma.DedicatedAsr);
        Assert.True(qwen06.DedicatedAsr);
        Assert.True(qwen17.DedicatedAsr);
    }

    [Fact]
    public void IsDedicatedAsrMatchesOnlyQwen3AsrModels()
    {
        Assert.True(LocalModels.IsDedicatedAsr("Qwen3-ASR-0.6B-Q8_0.gguf"));
        Assert.True(LocalModels.IsDedicatedAsr("Qwen3-ASR-1.7B-Q8_0.gguf"));
        Assert.False(LocalModels.IsDedicatedAsr("gemma-4-E2B-it-Q4_0.gguf"));
        Assert.False(LocalModels.IsDedicatedAsr("custom-model.gguf"));
        Assert.False(LocalModels.IsDedicatedAsr(null));
    }

    [Fact]
    public void DedicatedAsrServerArgsUseGreedySampling()
    {
        var server = CreateConfiguredServer("Qwen3-ASR-0.6B-Q8_0.gguf");

        var args = server.ServerArgs();

        Assert.Contains("--temp 0", args);
        Assert.DoesNotContain("--min-p", args);
        Assert.DoesNotContain("--repeat-penalty", args);
    }

    [Fact]
    public void LlmServerArgsKeepChatSamplingForNonAsrModels()
    {
        var server = CreateConfiguredServer("gemma-4-E2B-it-Q4_0.gguf");

        var args = server.ServerArgs();

        Assert.Contains($"--temp {AppConfig.Temperature}", args);
        Assert.Contains($"--min-p {AppConfig.MinP}", args);
    }

    [Fact]
    public async Task FormContentOmitsPromptForDedicatedAsrModels()
    {
        var service = new TranscriptionService(CreateLocalProvider("Qwen3-ASR-0.6B-Q8_0.gguf"));

        using var content = service.BuildFormContent(Array.Empty<byte>(), "Qwen3-ASR-0.6B-Q8_0.gguf");
        var body = await content.ReadAsStringAsync();

        Assert.Contains("name=model", body);
        // llama.cpp replaces its built-in ASR instruction with any non-empty
        // prompt, so dedicated ASR models must not receive one.
        Assert.DoesNotContain("name=prompt", body);
    }

    [Fact]
    public async Task FormContentIncludesPromptForLlmModels()
    {
        var service = new TranscriptionService(CreateLocalProvider("gemma-4-E2B-it-Q4_0.gguf"));

        using var content = service.BuildFormContent(Array.Empty<byte>(), "gemma-4-E2B-it-Q4_0.gguf");
        var body = await content.ReadAsStringAsync();

        Assert.Contains("name=prompt", body);
    }

    [Fact]
    public void ResolvePrefersExplicitSelectionOverCurrentModel()
    {
        var option = LocalModels.Resolve("qwen3-asr-1.7b", "gemma-4-E2B-it-Q4_0.gguf");
        Assert.Equal("qwen3-asr-1.7b", option!.Id);
    }

    [Fact]
    public void ResolveFallsBackToCurrentModelForLegacySelection()
    {
        var option = LocalModels.Resolve(null, "Qwen3-ASR-0.6B-Q8_0.gguf");
        Assert.Equal("qwen3-asr-0.6b", option!.Id);
    }

    [Fact]
    public void ResolveReturnsNullForUnknownModel()
    {
        Assert.Null(LocalModels.Resolve(null, "custom-model.gguf"));
    }

    [Fact]
    public void ResolveFallsBackToCurrentModelWhenSelectionIsUnknown()
    {
        var option = LocalModels.Resolve("not-in-catalog", "gemma-4-E2B-it-Q4_0.gguf");
        Assert.Equal(LocalModels.DefaultId, option!.Id);
    }

    [Fact]
    public void SetLocalModelUpdatesProviderAndSelection()
    {
        var state = CreateState();
        Assert.True(state.SetLocalModel("qwen3-asr-0.6b"));

        var local = state.LocalProvider!;
        Assert.Equal("Qwen3-ASR 0.6B (local)", local.Name);
        Assert.Equal("Qwen3-ASR-0.6B-Q8_0.gguf", local.Model);
        Assert.Equal("unslothai/Qwen3-ASR-0.6B-GGUF", local.HfRepo);
        Assert.Equal("mmproj-Qwen3-ASR-0.6B-Q8_0.gguf", local.Mmproj);
        Assert.Equal("qwen3-asr-0.6b", state.LocalModelId);

        // Applying the same selection again is a no-op.
        Assert.False(state.SetLocalModel("qwen3-asr-0.6b"));
    }

    [Fact]
    public void SetLocalModelIsNoOpWhenAlreadySelected()
    {
        var state = CreateState();
        Assert.False(state.SetLocalModel(LocalModels.DefaultId));
    }

    [Fact]
    public void SetLocalModelRejectsUnknownId()
    {
        var state = CreateState();
        Assert.False(state.SetLocalModel("does-not-exist"));
        Assert.Equal("gemma-4-E2B-it-Q4_0.gguf", state.LocalProvider!.Model);
    }

    static AppState CreateState()
    {
        var settings = new AppSettings
        {
            LocalModelId = LocalModels.DefaultId,
            Providers =
            {
                new ProviderConfig
                {
                    Name = "Gemma 4 E2B UD (local)",
                    Type = "local",
                    ApiEndpoint = "http://localhost:8082",
                    Model = "gemma-4-E2B-it-Q4_0.gguf",
                    Mmproj = "mmproj-BF16.gguf",
                    HfRepo = "unsloth/gemma-4-E2B-it-GGUF"
                }
            }
        };
        return new AppState(settings);
    }

    static LlmServer CreateConfiguredServer(string model)
    {
        var server = new LlmServer();
        server.Configure(CreateLocalProvider(model));
        return server;
    }

    static ProviderConfig CreateLocalProvider(string model) => new()
    {
        Name = "Local",
        Type = "local",
        ApiEndpoint = "http://localhost:8082",
        Model = model
    };
}
