using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    /// <summary>What a prepare produced: the table, and every file it did NOT manage to read.</summary>
    public sealed class LogPrepareResult
    {
        public required string TableName { get; init; }

        public IReadOnlyList<NotIngestedFile> NotIngested { get; init; } = Array.Empty<NotIngestedFile>();

        /// <summary>False whenever a file was skipped or failed — the rows returned are a partial answer.</summary>
        public bool IsComplete => NotIngested.Count == 0;
    }

    /// <summary>One file whose rows are absent from the table, and why.</summary>
    public sealed class NotIngestedFile
    {
        /// <summary>
        /// The <see cref="Reason"/> for a file a Cancel stopped partway. Unlike every other reason, its
        /// rows up to that point are in the table — a cancelled load keeps what it read.
        /// </summary>
        public const string CancelledPartwayReason = "partly read — search cancelled";

        /// <summary>The <see cref="Reason"/> for a file a Cancel stopped before a byte of it arrived.</summary>
        public const string CancelledBeforeReadingReason = "not read — search cancelled";

        public required string FileName { get; init; }
        public required string LocationName { get; init; }
        public FileIngestState State { get; init; }
        public long BytesRead { get; init; }
        public long BytesTotal { get; init; }

        /// <summary>
        /// This file's rows that are in the table anyway. 0 for every reason but
        /// <see cref="CancelledPartwayReason"/>: a skipped file's rows are purged (D4), and a failed
        /// file's are whatever it managed before failing.
        /// </summary>
        public long RowsInTable { get; init; }
        public required string Reason { get; init; }
    }
}
