using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

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

    /// <summary>
    /// Set when the workspace was discovered by a <see cref="WorkspaceSource"/> scan rather
    /// than added by hand. Scanned workspaces are rebuilt on every scan, so they are never
    /// persisted and cannot be edited from the dashboard.
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public string? SourceName { get; set; }

    [YamlIgnore]
    [JsonIgnore]
    public bool IsFromSource => !string.IsNullOrEmpty(SourceName);
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

    /// <summary>One-line subtitle pulled out of the file by a scan. Display only.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public string? Description { get; set; }
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

    /// <summary>
    /// Asks another program where the executable lives, instead of hardcoding a path.
    /// Used when the install moves between versions or when the registry points at the
    /// wrong one — <c>vswhere -latest -property productPath</c> being the motivating case.
    /// Takes precedence over <see cref="ExecutablePath"/>, which stays as the fallback.
    /// </summary>
    [JsonPropertyName("executableFrom")]
    public ExecutableLocator? ExecutableFrom { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

/// <summary>
/// A command whose first line of output is the path to an executable.
/// </summary>
public class ExecutableLocator
{
    /// <summary>Program to run. Resolved the same way as any other executable name.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

public class GlobalCustomOpenOptions
{
    [JsonPropertyName("options")]
    public Dictionary<string, CustomOpenOption> Options { get; set; } = new();
}