using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models
{
    public class LogTemplate
    {
        public string Name { get; set; } = "";
        public string Extension { get; set; } = ".txt";

        /// <summary>
        /// Another template's file name — without the <c>.yaml</c> — whose columns come before this
        /// one's. Omitted from the YAML when unset: these files are read and edited by hand, and a
        /// bare <c>inherits:</c> line reads as a setting someone started and abandoned.
        /// </summary>
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? Inherits { get; set; }

        public string Delimiter { get; set; } = "|";
        public List<string> Columns { get; set; } = new();

        /// <summary>
        /// Multi-column sort applied unless the caller asks for its own. Omitted when unset, for the
        /// same reason as <see cref="Inherits"/>.
        /// </summary>
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public List<SortColumn>? Sort { get; set; }
    }

    public class SortColumn
    {
        public string Column { get; set; } = "";
        public string Direction { get; set; } = "asc";
    }
}
