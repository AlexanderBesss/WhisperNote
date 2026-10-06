using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class LocalModelTests
{
    // Stands in for any model outside the catalog (legacy or custom configs):
    // exercises the generic non-dedicated-ASR code paths.
    const string CustomLlmModel = "custom-llm.gguf";

    [Fact]
    public void CatalogContainsQwen3Asr17B()
    {
        Assert.Single(LocalModels.All);

        var qwen17 = LocalModels.FindById(LocalModels.DefaultId);
        Assert.NotNull(qwen17);
        Assert.Equal("Qwen3-ASR-1.7B-Q8_0.gguf", qwen17!.Model);
        Assert.Equal("unslothai/Qwen3-ASR-1.7B-GGUF", qwen17.HfRepo);
        Assert.Equal("mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", qwen17.Mmproj);
        Assert.True(qwen17.DedicatedAsr);
    }

    [Fact]
    public void IsDedicatedAsrMatchesOnlyQwen3AsrModels()
    {
        Assert.True(LocalModels.IsDedicatedAsr("Qwen3-ASR-1.7B-Q8_0.gguf"));
        Assert.False(LocalModels.IsDedicatedAsr(CustomLlmModel));
        Assert.False(LocalModels.IsDedicatedAsr(null));
    }

    [Fact]
    public void DedicatedAsrServerArgsUseGreedySampling()
    {
        var server = CreateConfiguredServer("Qwen3-ASR-1.7B-Q8_0.gguf");

        var args = server.ServerArgs();

        Assert.Contains("--temp 0", args);
        Assert.DoesNotContain("--min-p", args);
        Assert.DoesNotContain("--repeat-penalty", args);
    }

    [Fact]
    public void LlmServerArgsKeepChatSamplingForNonAsrModels()
    {
        var server = CreateConfiguredServer(CustomLlmModel);

        var args = server.ServerArgs();

        Assert.Contains($"--temp {AppConfig.Temperature}", args);
        Assert.Contains($"--min-p {AppConfig.MinP}", args);
    }

    [Fact]
    public async Task FormContentOmitsPromptForDedicatedAsrModels()
    {
        var service = new TranscriptionService(CreateLocalProvider("Qwen3-ASR-1.7B-Q8_0.gguf"));

        using var content = service.BuildFormContent(Array.Empty<byte>(), "Qwen3-ASR-1.7B-Q8_0.gguf");
        var body = await content.ReadAsStringAsync();

        Assert.Contains("name=model", body);
        // llama.cpp replaces its built-in ASR instruction with any non-empty
        // prompt, so dedicated ASR models must not receive one.
        Assert.DoesNotContain("name=prompt", body);
    }

    [Fact]
    public async Task FormContentIncludesPromptForLlmModels()
    {
        var service = new TranscriptionService(CreateLocalProvider(CustomLlmModel));

        using var content = service.BuildFormContent(Array.Empty<byte>(), CustomLlmModel);
        var body = await content.ReadAsStringAsync();

        Assert.Contains("name=prompt", body);
    }

    [Fact]
    public void ResolvePrefersExplicitSelectionOverCurrentModel()
    {
        var option = LocalModels.Resolve("qwen3-asr-1.7b", CustomLlmModel);
        Assert.Equal("qwen3-asr-1.7b", option!.Id);
    }

    [Fact]
    public void ResolveFallsBackToCurrentModelForLegacySelection()
    {
        var option = LocalModels.Resolve(null, "Qwen3-ASR-1.7B-Q8_0.gguf");
        Assert.Equal("qwen3-asr-1.7b", option!.Id);
    }

    [Fact]
    public void ResolveReturnsNullForUnknownModel()
    {
        Assert.Null(LocalModels.Resolve(null, CustomLlmModel));
    }

    [Fact]
    public void ResolveFallsBackToCurrentModelWhenSelectionIsUnknown()
    {
        var option = LocalModels.Resolve("not-in-catalog", "Qwen3-ASR-1.7B-Q8_0.gguf");
        Assert.Equal("qwen3-asr-1.7b", option!.Id);
    }

    [Fact]
    public void SetLocalModelUpdatesProviderAndSelection()
    {
        var state = CreateState();
        Assert.True(state.SetLocalModel("qwen3-asr-1.7b"));

        var local = state.LocalProvider!;
        Assert.Equal("Qwen3-ASR 1.7B (local)", local.Name);
        Assert.Equal("Qwen3-ASR-1.7B-Q8_0.gguf", local.Model);
        Assert.Equal("unslothai/Qwen3-ASR-1.7B-GGUF", local.HfRepo);
        Assert.Equal("mmproj-Qwen3-ASR-1.7B-Q8_0.gguf", local.Mmproj);
        Assert.Equal("qwen3-asr-1.7b", state.LocalModelId);

        // Applying the same selection again is a no-op.
        Assert.False(state.SetLocalModel("qwen3-asr-1.7b"));
    }

    [Fact]
    public void SetLocalModelRejectsUnknownId()
    {
        var state = CreateState();
        Assert.False(state.SetLocalModel("does-not-exist"));
        Assert.Equal(CustomLlmModel, state.LocalProvider!.Model);
    }

    static AppState CreateState()
    {
        var settings = new AppSettings
        {
            LocalModelId = null,
            Providers =
            {
                new ProviderConfig
                {
                    Name = "Custom (local)",
                    Type = "local",
                    ApiEndpoint = "http://localhost:8082",
                    Model = CustomLlmModel
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
