using System.Text.Json.Serialization;

namespace DevToolbox.Services.Models;

public class Workspace
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}