using System.Text.Json.Serialization;

namespace DevToolbox.Services.Models;

public class Workspace
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("locations")]
    public List<WorkspaceLocation> Locations { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastAccessed")]
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
}

public class WorkspaceLocation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public LocationType Type { get; set; }
}

public enum LocationType
{
    [JsonPropertyName("file")]
    File,
    [JsonPropertyName("folder")]
    Folder,
    [JsonPropertyName("solution")]
    Solution,
    [JsonPropertyName("project")]
    Project
}

public enum OpenOptionType
{
    [JsonPropertyName("executable")]
    Executable,
    [JsonPropertyName("bash")]
    Bash
}

public class CustomOpenOption
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public OpenOptionType Type { get; set; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

public class GlobalCustomOpenOptions
{
    [JsonPropertyName("options")]
    public Dictionary<string, CustomOpenOption> Options { get; set; } = new();
}