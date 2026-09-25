using System.Collections.Generic;
using System.Linq;

namespace WhisperNote.Config;

public sealed class LocalModelOption
{
    public string Id { get; }
    public string Name { get; }
    public string Model { get; }
    public string HfRepo { get; }
    public string? Mmproj { get; }

    // True for purpose-built ASR models (Qwen3-ASR). They expect the server's
    // built-in transcription instruction and greedy decoding, not a custom
    // LLM-style prompt or chat sampling parameters.
    public bool DedicatedAsr { get; }

    public LocalModelOption(string id, string name, string model, string hfRepo, string? mmproj, bool dedicatedAsr)
    {
        Id = id;
        Name = name;
        Model = model;
        HfRepo = hfRepo;
        Mmproj = mmproj;
        DedicatedAsr = dedicatedAsr;
    }

    public string ProviderName => $"{Name} (local)";

    public override string ToString() => Name;
}

public static class LocalModels
{
    public const string DefaultId = "gemma-4-e2b";

    public static readonly IReadOnlyList<LocalModelOption> All = new[]
    {
        new LocalModelOption(
            DefaultId,
            "Gemma 4 E2B UD",
            "gemma-4-E2B-it-Q4_0.gguf",
            "unsloth/gemma-4-E2B-it-GGUF",
            "mmproj-BF16.gguf",
            dedicatedAsr: false),
        new LocalModelOption(
            "qwen3-asr-1.7b",
            "Qwen3-ASR 1.7B",
            "Qwen3-ASR-1.7B-Q8_0.gguf",
            "unslothai/Qwen3-ASR-1.7B-GGUF",
            "mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
            dedicatedAsr: true),
    };

    public static LocalModelOption? FindById(string? id) =>
        All.FirstOrDefault(option => option.Id == id);

    public static LocalModelOption? FindByModel(string? model) =>
        All.FirstOrDefault(option => option.Model == model);

    public static bool IsDedicatedAsr(string? model) =>
        FindByModel(model)?.DedicatedAsr == true;

    // The explicit selection wins; otherwise fall back to matching the model
    // file currently configured on the local provider (legacy or custom configs).
    public static LocalModelOption? Resolve(string? selectedId, string? currentModel) =>
        FindById(selectedId) ?? FindByModel(currentModel);
}
