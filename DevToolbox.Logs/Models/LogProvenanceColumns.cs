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

        /// <summary>
        /// <paramref name="columns"/> with the provenance columns moved to the end, in
        /// <see cref="All"/>'s order, and everything else left in the order given.
        /// <para>
        /// The ingest creates a table with the template's columns and provenance, and adds an
        /// overflow column (<c>Message1</c>, …) the first time a line needs one — which SQLite can
        /// only put after the columns already there. Physically, then, <c>Message3</c> can follow
        /// <c>SourcePath</c>. Every reader that shows columns runs them through this so they still
        /// read template, overflow, provenance, the order they always had.
        /// </para>
        /// </summary>
        public static List<string> InDisplayOrder(IEnumerable<string> columns)
        {
            var list = columns.ToList();
            var ordered = list.Where(c => !IsProvenance(c)).ToList();
            foreach (var provenance in All)
                ordered.AddRange(list.Where(c => string.Equals(c, provenance, StringComparison.OrdinalIgnoreCase)));
            return ordered;
        }
    }
}
