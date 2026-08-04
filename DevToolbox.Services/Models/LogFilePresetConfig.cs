using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class LogFilePresetConfig
    {
        public List<LogFilePresetGroup> Presets { get; set; } = new();
    }

    public class LogFilePresetGroup
    {
        // Template name these presets apply to (matches LogTemplateIndexEntry.Name).
        public string Template { get; set; } = "";

        // Optional file name to auto-fill when this template is selected (e.g. IIS "u_ex").
        public string? DefaultFile { get; set; }

        public List<string> Files { get; set; } = new();
    }
}
