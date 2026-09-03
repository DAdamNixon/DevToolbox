using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    /// <summary>The whole of <c>saved_queries.yaml</c>. One key, so the file can grow another later.</summary>
    public class SavedQueryConfig
    {
        public List<SavedQuery> Queries { get; set; } = new();
    }

    /// <summary>
    /// One query written in the Log Viewer's advanced (SQL) mode, kept so it can be run again.
    /// <para>
    /// The group is a plain string on the query rather than a nesting level, so moving a query
    /// between groups is one field edit and the picker's grouping is a <c>GroupBy</c>. The cost is
    /// that a group rename has to touch every member — which is why that is a service method and
    /// not something each caller loops over itself.
    /// </para>
    /// </summary>
    public class SavedQuery
    {
        /// <summary>
        /// Stable identity, assigned on first save. Names and groups are both editable, so neither
        /// can be the key: the filter bar holds on to whichever query it loaded, and a rename must
        /// not turn that into a dangling reference.
        /// </summary>
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>Empty means ungrouped, which the UI shows under its own heading rather than hiding.</summary>
        public string Group { get; set; } = "";

        public string Sql { get; set; } = "";

        public string? Description { get; set; }

        /// <summary>
        /// The log template that was selected when the query was written. A hint, never a filter:
        /// the ingested table is called <c>logs</c> whatever parsed it, so a query saved under one
        /// template still runs under another — it just probably names columns that are not there.
        /// </summary>
        public string? Template { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }
}
