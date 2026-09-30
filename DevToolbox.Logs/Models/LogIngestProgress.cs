using System;
using System.Collections.Generic;

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
        /// Reading the head of each matched file to work out the columns. No longer entered by
        /// <c>DbLogService</c>: the table now starts with the template's columns and gains an
        /// overflow column the first time a line needs one, so rows can be stored — and shown —
        /// from the first file on, and no file is opened twice. Kept so a caller naming the value
        /// still compiles.
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

        /// <summary>Rows committed to the table so far — including any a skip has since taken back out.</summary>
        public long RowsIngested { get; init; }

        /// <summary>
        /// Rows deleted again because the file they came from was skipped (D4). The table holds
        /// <see cref="RowsIngested"/> minus this; see <see cref="RowsInTable"/>.
        /// </summary>
        public long RowsPurged { get; init; }

        /// <summary>What the table holds right now: committed rows less the ones a skip purged.</summary>
        public long RowsInTable => Math.Max(0, RowsIngested - RowsPurged);

        /// <summary>
        /// Rises by one on every insert batch and every purge the writer commits, and never
        /// otherwise. Two snapshots with the same value describe the same table contents, which is
        /// what lets a live view skip a refresh that could not show anything new — and notice a
        /// purge, which neither row count on its own would.
        /// </summary>
        public long TableVersion { get; init; }

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

        /// <summary>Files currently open — Opening, Reading or Stalled — for the per-file rows.</summary>
        public IReadOnlyList<FileProgressSnapshot> InFlight { get; init; } = Array.Empty<FileProgressSnapshot>();

        public int FilesStalled { get; init; }
        public int FilesSkipped { get; init; }
        public int FilesFailed { get; init; }

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
