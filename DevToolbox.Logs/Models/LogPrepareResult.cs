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
        public required string FileName { get; init; }
        public required string LocationName { get; init; }
        public FileIngestState State { get; init; }
        public long BytesRead { get; init; }
        public long BytesTotal { get; init; }
        public required string Reason { get; init; }
    }
}
