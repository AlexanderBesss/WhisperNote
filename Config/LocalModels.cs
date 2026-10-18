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

    // Dedicated ASR models require the server's built-in transcription instruction and greedy decoding, not custom prompts.
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
    public const string DefaultId = "qwen3-asr-1.7b";

    public static readonly IReadOnlyList<LocalModelOption> All = new[]
    {
        new LocalModelOption(
            DefaultId,
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

    public static LocalModelOption? Resolve(string? selectedId, string? currentModel) =>
        FindById(selectedId) ?? FindByModel(currentModel);
}
