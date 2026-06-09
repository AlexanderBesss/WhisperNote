using System.Text.Json.Serialization;

namespace WhisperNote.Config;

public class ProviderConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "local";
    public string ApiEndpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Mmproj { get; set; }
    public string? ServerExe { get; set; }

    [JsonIgnore]
    public bool IsLocal => Type == "local";

    public override string ToString() => Name;
}
