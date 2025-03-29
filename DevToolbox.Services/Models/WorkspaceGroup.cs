using System.Text.Json.Serialization;

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
    }
} 