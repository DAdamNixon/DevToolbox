using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models
{
    public class WorkspaceGroup
    {
        [JsonPropertyName("id")]
        public required int Id { get; set; }

        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("workspaces")]
        public List<Workspace> Workspaces { get; set; } = new();

        /// <summary>
        /// Set when the group is the product of a <see cref="WorkspaceSource"/> scan.
        /// Such groups are virtual: they are never written to workspaceGroups.yaml and the
        /// dashboard hides the edit actions for them.
        /// </summary>
        [YamlIgnore]
        [JsonIgnore]
        public string? SourceName { get; set; }

        [YamlIgnore]
        [JsonIgnore]
        public bool IsFromSource => !string.IsNullOrEmpty(SourceName);

        /// <summary>Folder the scan came from, so the UI can offer "open source folder".</summary>
        [YamlIgnore]
        [JsonIgnore]
        public string? SourcePath { get; set; }

        /// <summary>Icon declared by the source, used when no icon rule matches.</summary>
        [YamlIgnore]
        [JsonIgnore]
        public IconStyle? SourceIcon { get; set; }
    }
} 