using System;
using System.Collections.Generic;
using System.Linq;

namespace DevToolbox.Services.Models
{
    /// <summary>
    /// The columns the ingest appends about <em>where</em> a row came from, as opposed to what the
    /// file said.
    /// <para>
    /// Named here rather than as literals at each use because three separate things need to agree
    /// about them: the ingest that adds them, the grid that hides <c>SourcePath</c> and leaves the
    /// rest out of text mode, and the template editor, which has to stop someone declaring a column
    /// called <c>Location</c> and silently colliding with the one the ingest is going to add.
    /// </para>
    /// </summary>
    public static class LogProvenanceColumns
    {
        public const string Location = "Location";
        public const string SourceFile = "SourceFile";
        public const string Sequence = "Sequence";
        public const string SourcePath = "SourcePath";

        /// <summary>In the order the ingest appends them.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Location, SourceFile, Sequence, SourcePath };

        /// <summary>
        /// The three worth reading. <c>SourcePath</c> is left out: it is how a row gets opened in an
        /// editor, not something to look at or sort by.
        /// </summary>
        public static readonly IReadOnlyList<string> Visible = new[] { Location, SourceFile, Sequence };

        public static bool IsProvenance(string? column) =>
            column is not null && All.Contains(column, StringComparer.OrdinalIgnoreCase);
    }
}
