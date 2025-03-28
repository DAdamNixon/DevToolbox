using System.Text.Json.Serialization;

namespace DevToolbox.Services.Models;

public class DirectoryStructure
{
    [JsonPropertyName("rootPath")]
    public string RootPath { get; set; } = string.Empty;

    [JsonPropertyName("filePattern")]
    public string FilePattern { get; set; } = string.Empty;

    [JsonPropertyName("includeDirectories")]
    public bool IncludeDirectories { get; set; }

    [JsonPropertyName("includeHidden")]
    public bool IncludeHidden { get; set; }

    [JsonPropertyName("includeSystem")]
    public bool IncludeSystem { get; set; }

    [JsonPropertyName("structure")]
    public DirectoryNode Structure { get; set; } = new();
}

public class DirectoryNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FileInfo> Files { get; set; } = new();

    [JsonPropertyName("directories")]
    public List<DirectoryNode> Directories { get; set; } = new();
}

public class FileInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; }

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public string Attributes { get; set; } = string.Empty;
} 