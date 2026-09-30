using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    /// <summary>
    /// A run of rowids one insert batch was given. The writer is the only writer and each batch is
    /// one transaction, so a batch's rowids are contiguous — which is what lets a skipped file's
    /// rows be purged by range, in time proportional to the file rather than a scan of the table.
    /// </summary>
    public readonly record struct LogRowRange(long First, long Last)
    {
        public long Count => Last >= First ? Last - First + 1 : 0;
    }

    /// <summary>
    /// What a live view asks of a table that is still being filled: everything that arrived after
    /// <see cref="AfterRowid"/>, up to the newest committed row, read in one snapshot.
    /// <para>
    /// Rows are appended in rowid order and never moved, so "what changed since last time" is a
    /// rowid range, and every figure here costs time in proportion to the rows that are new — not
    /// to the table, which during a large load is the difference between a refresh that is free and
    /// one that is a full scan every second. <see cref="AfterRowid"/> 0 reads from the start: the
    /// first refresh, and every one after a user changes what they are looking at.
    /// </para>
    /// </summary>
    public sealed class LogLiveRequest
    {
        /// <summary>Only rows with a rowid above this. 0 for everything.</summary>
        public long AfterRowid { get; init; }

        /// <summary>The keyword filter, or null for every row. Never SQL mode — its own query decides its order and cost.</summary>
        public LogSearchCriteria? Criteria { get; init; }

        /// <summary>The active tab, if any. Applies to <see cref="LogLiveSlice.Count"/> and the rows, not to the groups.</summary>
        public LogSplitFilter? Split { get; init; }

        /// <summary>The split column to count per value, or null when not splitting.</summary>
        public string? GroupColumn { get; init; }

        /// <summary>
        /// How many matching rows in the range to pass over before <see cref="Take"/> starts, in
        /// <see cref="Sort"/> order — or in arrival order when there is no sort.
        /// </summary>
        public int Skip { get; init; }

        /// <summary>How many rows to return. 0 returns none, for a refresh that only needs the counts.</summary>
        public int Take { get; init; }

        /// <summary>
        /// Order for the rows, or null for the order they arrived in (<c>rowid ASC</c>). A sort is a
        /// full pass over everything read so far, so it is only ever asked for with
        /// <see cref="AfterRowid"/> 0 — once, when someone clicks a header — never on a timer.
        /// </summary>
        public List<SortColumn>? Sort { get; init; }
    }

    /// <summary>One snapshot's answer to a <see cref="LogLiveRequest"/>.</summary>
    public sealed class LogLiveSlice
    {
        /// <summary>The newest rowid the snapshot saw; the next refresh asks for rows after it. 0 on an empty table.</summary>
        public long MaxRowid { get; init; }

        /// <summary>Matching rows in the range — criteria and tab applied.</summary>
        public int Count { get; init; }

        /// <summary>Per-value counts of the group column in the range, criteria applied, tab not. Empty when not grouping.</summary>
        public List<LogSplitGroup> Groups { get; init; } = new();

        public List<Dictionary<string, string>> Rows { get; init; } = new();
    }

    /// <summary>
    /// A prepare that was cancelled, carrying what it had done by then.
    /// <para>
    /// An <see cref="OperationCanceledException"/>, so every caller that already treats a cancel as a
    /// cancel keeps doing so. The Log Viewer additionally reads <see cref="Result"/>: a cancelled load
    /// keeps the rows it had read, and without the per-file account a grid that stops partway
    /// through three files would look complete — against this viewer's own rule that a missing row
    /// is not evidence it did not happen.
    /// </para>
    /// </summary>
    public sealed class LogIngestCancelledException : OperationCanceledException
    {
        public LogIngestCancelledException(LogPrepareResult result, bool tableReplaced, Exception? inner = null)
            : base("The log load was cancelled.", inner)
        {
            Result = result;
            TableReplaced = tableReplaced;
        }

        /// <summary>Every file the load did not finish — cut short, never opened, skipped or failed.</summary>
        public LogPrepareResult Result { get; }

        /// <summary>
        /// True when the previous table had already been dropped: whatever rows the table holds now
        /// came from this load. False when the cancel landed before that, and the previous search's
        /// table is untouched.
        /// </summary>
        public bool TableReplaced { get; }
    }
}
