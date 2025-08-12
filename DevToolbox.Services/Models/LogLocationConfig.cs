using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class LogLocationConfig
    {
        public List<LogLocation> LogLocations { get; set; } = new();
    }

    public class LogLocation
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }
}