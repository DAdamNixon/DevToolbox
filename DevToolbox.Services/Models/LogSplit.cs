using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    /// <summary>How the result set is divided into tabs.</summary>
    public enum LogSplitMode
    {
        /// <summary>One table, everything in it. The original behaviour.</summary>
        None,

        /// <summary>A tab per configured location the rows came from.</summary>
        Location,

        /// <summary>A tab per source file.</summary>
        File
    }

    /// <summary>
    /// The column each split mode groups on.
    /// <para>
    /// These names reach SQL as identifiers rather than parameters, so the mapping
    /// is a closed set on purpose — a split column is never free text, and
    /// <see cref="TryResolve"/> is the only way to obtain one.
    /// </para>
    /// </summary>
    public static class LogSplitColumns
    {
        public const string Location = "Location";
        public const string SourceFile = "SourceFile";

        public static bool TryResolve(LogSplitMode mode, out string column)
        {
            switch (mode)
            {
                case LogSplitMode.Location:
                    column = Location;
                    return true;
                case LogSplitMode.File:
                    column = SourceFile;
                    return true;
                default:
                    column = string.Empty;
                    return false;
            }
        }

        /// <summary>Guards a column name on its way into SQL.</summary>
        public static bool IsAllowed(string? column) =>
            string.Equals(column, Location, StringComparison.Ordinal) ||
            string.Equals(column, SourceFile, StringComparison.Ordinal);
    }

    /// <summary>One distinct value of the split column, and how many rows carry it.</summary>
    public class LogSplitGroup
    {
        public string Value { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>
    /// Restricts a query to one tab. Null means the "All" tab, which is simply the
    /// absence of this predicate rather than a special case anywhere downstream.
    /// </summary>
    public class LogSplitFilter
    {
        public string Column { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public static LogSplitFilter? For(LogSplitMode mode, string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!LogSplitColumns.TryResolve(mode, out var column)) return null;
            return new LogSplitFilter { Column = column, Value = value };
        }

        /// <summary>Shape expected by <see cref="LogQuery.Filters"/>.</summary>
        public Dictionary<string, object> ToFilters() => new() { [Column] = Value };
    }
}
