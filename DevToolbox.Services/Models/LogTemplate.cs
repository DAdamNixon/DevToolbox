using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class LogTemplate
    {
        public string Name { get; set; } = "";
        public string Extension { get; set; } = ".txt";
        public string? Inherits { get; set; }
        public string Delimiter { get; set; } = "|";
        public List<string> Columns { get; set; } = new();
        // New property for multi-column sorting
        public List<SortColumn>? Sort { get; set; }
    }

    public class SortColumn
    {
        public string Column { get; set; } = "";
        public string Direction { get; set; } = "asc";
    }
}
