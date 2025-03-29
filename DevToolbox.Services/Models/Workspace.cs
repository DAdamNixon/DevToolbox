using System.Text.Json.Serialization;

namespace DevToolbox.Services.Models;

public class Workspace
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = string.Empty;

    [JsonPropertyName("locations")]
    public List<WorkspaceLocation> Locations { get; set; } = new();
}

public class WorkspaceLocation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

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
    [JsonPropertyName("command")]
    Command
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