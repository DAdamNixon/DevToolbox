using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class LogTemplate
    {
        public string Name { get; set; } = "";
        public string? Inherits { get; set; }
        public string Delimiter { get; set; } = "|";
        public List<string> Columns { get; set; } = new();
    }
}