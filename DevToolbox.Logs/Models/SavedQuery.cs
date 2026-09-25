using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

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

        /// <summary>
        /// Which filter card this query belongs to — <see cref="SavedQueryTargets.Logs"/> or
        /// <see cref="SavedQueryTargets.Results"/>. Null, blank and <c>logs</c> all normalise to
        /// null on save (<see cref="SavedQueryTargets.IsFor"/> treats null as <c>logs</c>), so every
        /// query saved before <c>results</c> existed still reads as a logs query and writes no new
        /// key.
        /// </summary>
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public string? Target { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>The two filter cards a saved query can belong to.</summary>
    public static class SavedQueryTargets
    {
        public const string Logs = "logs";
        public const string Results = "results";

        /// <summary>True when <paramref name="query"/>'s target is <paramref name="target"/> —
        /// null, blank and <see cref="Logs"/> all mean the same thing.</summary>
        public static bool IsFor(SavedQuery query, string target) =>
            string.Equals(Normalize(query.Target), Normalize(target), StringComparison.Ordinal);

        /// <summary>Null, blank and <see cref="Logs"/> all collapse to null — nothing new to write
        /// for the common case, and every file on disk before this field existed still reads right.</summary>
        public static string? Normalize(string? target)
        {
            var trimmed = (target ?? "").Trim();
            return trimmed.Length == 0 || string.Equals(trimmed, Logs, StringComparison.OrdinalIgnoreCase)
                ? null
                : trimmed;
        }
    }
}
