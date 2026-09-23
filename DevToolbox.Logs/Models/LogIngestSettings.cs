namespace DevToolbox.Services.Models
{
    /// <summary>
    /// How the Log Viewer decides a file has stalled, and what it does about it. Lives in its own
    /// YAML (<c>log_ingest_settings</c>) rather than <c>UiSettings</c>, because <c>UiSettings</c> is
    /// in <c>DevToolbox.Services</c>, which <c>DevToolbox.Mcp</c> must not load.
    /// </summary>
    public sealed class LogIngestSettings
    {
        /// <summary>Seconds with no new bytes before a file is flagged Stalled. Same everywhere.</summary>
        public int StallThresholdSeconds { get; set; } = 10;

        /// <summary>Off by default: a person is watching the progress bar and decides.</summary>
        public bool UiAutoSkipEnabled { get; set; } = false;

        /// <summary>Seconds of stall before the UI auto-skips, once <see cref="UiAutoSkipEnabled"/> is on.</summary>
        public int UiAutoSkipTimeoutSeconds { get; set; } = 60;

        /// <summary>Always applied for the MCP server — nobody is there to click Skip.</summary>
        public int McpAutoSkipTimeoutSeconds { get; set; } = 60;
    }
}
