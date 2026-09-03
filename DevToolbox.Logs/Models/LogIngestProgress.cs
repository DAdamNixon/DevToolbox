using System;

namespace DevToolbox.Services.Models
{
    /// <summary>What the Log Viewer is doing right now.</summary>
    public enum LogIngestPhase
    {
        /// <summary>
        /// Walking the configured directories to find files that match the name and
        /// date filters.
        /// <para>
        /// Its own phase because on a large share it dominates the wait: the archive
        /// this was built against holds 238,000 files, and listing it takes longer
        /// than reading the handful that match. Nothing is known up front here — not
        /// the file count, not the byte total — so this phase reports what it has
        /// examined rather than a percentage.
        /// </para>
        /// </summary>
        Listing,

        /// <summary>
        /// Reading the head of each matched file to work out the columns. Separate
        /// from ingest because it happens before a single row is stored.
        /// </summary>
        Scanning,

        /// <summary>Parsing files and writing rows into SQLite.</summary>
        Ingesting,

        /// <summary>Ingest finished; running the count and page queries.</summary>
        Querying
    }

    /// <summary>
    /// A snapshot of ingest progress, for the bar and the estimate.
    /// <para>
    /// Progress is measured in <em>bytes</em>, not files. Log file sizes across a
    /// date range differ by orders of magnitude, so "12 of 380 files" says almost
    /// nothing about how much work is left, and an estimate built on it swings
    /// wildly. File counts are still carried because they are what a person
    /// recognises.
    /// </para>
    /// </summary>
    public sealed class LogIngestProgress
    {
        public LogIngestPhase Phase { get; init; }

        public int FilesTotal { get; init; }
        public int FilesDone { get; init; }

        public long BytesTotal { get; init; }
        public long BytesDone { get; init; }

        public long RowsIngested { get; init; }

        /// <summary>
        /// Directory entries looked at during <see cref="LogIngestPhase.Listing"/>.
        /// The only measure available there, since the total is unknown until the
        /// walk finishes — but a number that climbs is the difference between
        /// "working" and "hung".
        /// </summary>
        public long ItemsExamined { get; init; }

        /// <summary>
        /// What is being worked on: a file name in most phases, the location name
        /// while listing. Context, not necessarily the only thing in flight.
        /// </summary>
        public string? CurrentFile { get; init; }

        public TimeSpan Elapsed { get; init; }

        /// <summary>
        /// Estimated time remaining, or null while there is not yet enough evidence
        /// for the number to mean anything. Callers should say "estimating…" rather
        /// than show a zero.
        /// </summary>
        public TimeSpan? Eta { get; init; }

        /// <summary>0-100. Falls back to file count when byte totals are unavailable.</summary>
        public double PercentComplete
        {
            get
            {
                if (BytesTotal > 0) return Math.Clamp((double)BytesDone / BytesTotal * 100, 0, 100);
                if (FilesTotal > 0) return Math.Clamp((double)FilesDone / FilesTotal * 100, 0, 100);
                return 0;
            }
        }
    }
}
