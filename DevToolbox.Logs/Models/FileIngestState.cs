namespace DevToolbox.Services.Models
{
    /// <summary>What one file in the current ingest is doing right now.</summary>
    public enum FileIngestState
    {
        /// <summary>The share connect / file open has not returned a first byte yet.</summary>
        Opening,

        /// <summary>Bytes are arriving.</summary>
        Reading,

        /// <summary>No new bytes for the configured stall threshold. Still open; not yet given up on.</summary>
        Stalled,

        /// <summary>Read to the end and its rows are in the table.</summary>
        Done,

        /// <summary>Abandoned — by hand, by auto-skip, or by a whole-search Cancel. Contributes zero rows.</summary>
        Skipped,

        /// <summary>Could not be opened or read. The load continued without it.</summary>
        Failed
    }
}
