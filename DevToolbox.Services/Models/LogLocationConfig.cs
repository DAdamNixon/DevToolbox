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

        /// <summary>
        /// Optional regex describing this location's file names, used to offer the
        /// Log File box a list of what is actually there instead of making you know
        /// the answer already.
        /// <para>
        /// Must contain a named group <c>name</c> — that capture is what gets listed.
        /// Groups called <c>date</c> and <c>server</c> are conventional for the rest
        /// of the file name but nothing reads them; they exist so a pattern documents
        /// itself. Matched against the file name only, never the directory.
        /// </para>
        /// <para>
        /// Two locations can hold the same projects under different naming schemes —
        /// archived files carry the server, live ones do not — which is why the
        /// pattern belongs to the location rather than to the template or the app.
        /// A location without one keeps the old behaviour exactly: the preset list
        /// from log_file_presets.yaml, and free text.
        /// </para>
        /// <example><c>^(?&lt;name&gt;.+)\.(?&lt;date&gt;\d{8})\.WEB(?&lt;server&gt;\d+)\.txt$</c></example>
        /// </summary>
        public string? NamePattern { get; set; }
    }

    /// <summary>One distinct <c>name</c> capture, and how many files produced it.</summary>
    public class DiscoveredLogName
    {
        public string Name { get; set; } = "";

        /// <summary>Files matching this name across every searched location.</summary>
        public int FileCount { get; set; }
    }
}