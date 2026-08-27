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

        /// <summary>
        /// Every smart folder that has rows on this group, in the order they were scanned.
        /// <see cref="SourceName"/> is only the first of them, and a group fed by four rules —
        /// a plain pattern and a solution-filter pattern per branch — looked identical on the
        /// dashboard to one fed by a single rule. The card shows this count so the answer to
        /// "why is this card shaped like that?" starts with how many rules built it.
        /// </summary>
        [YamlIgnore]
        [JsonIgnore]
        public List<string> SourceNames { get; set; } = new();

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