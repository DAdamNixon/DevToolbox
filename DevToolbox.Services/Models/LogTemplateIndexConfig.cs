using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class LogTemplateIndexConfig
    {
        public List<LogTemplateIndexEntry> Templates { get; set; } = new();
    }

    public class LogTemplateIndexEntry
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
    }
}