using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// What a template has to be true of before it is worth saving.
    /// <para>
    /// Separate from the dialog that collects it because the cost of a bad template is not a bad
    /// dialog — it is a <c>CREATE TABLE</c> that fails, or worse, one that succeeds and quietly
    /// overwrites the ingest's own <c>Location</c> column. So the rules are here, where a test can
    /// reach them, and the dialog only renders what they say.
    /// </para>
    /// </summary>
    public static class LogTemplateValidator
    {
        /// <summary>Characters that would break out of the <c>[column]</c> quoting the SQL uses.</summary>
        private static readonly char[] IllegalInColumnName = { '[', ']' };

        /// <summary>
        /// Every problem with <paramref name="template"/>, in the order they appear in the form.
        /// Empty means it can be saved.
        /// </summary>
        /// <param name="otherTemplateNames">The names already taken by <em>other</em> templates —
        /// the caller excludes the one being edited, so re-saving it unchanged is not a clash.</param>
        /// <param name="inheritedColumns">Columns this template's base contributes, when it declares
        /// an <c>inherits</c>. The ingest merges those in front of the declared ones, so a name that
        /// looks unique here is still a duplicate SQL column if the base already had it — and a sort
        /// on a base column is perfectly legitimate. Null for a template that inherits nothing.</param>
        public static List<string> Validate(
            LogTemplate template,
            IEnumerable<string> otherTemplateNames,
            IEnumerable<string>? inheritedColumns = null)
        {
            var inherited = (inheritedColumns ?? Enumerable.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToList();

            var problems = new List<string>();

            var name = template.Name?.Trim() ?? "";
            if (name.Length == 0)
                problems.Add("Give the template a name.");
            else if (otherTemplateNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"Another template is already called \"{name}\".");

            var extension = template.Extension?.Trim() ?? "";
            if (extension.Length == 0)
                problems.Add("Give the template a file extension, for example .txt.");
            else if (!extension.StartsWith('.'))
                problems.Add("The extension has to start with a dot, for example .log.");
            else if (extension.IndexOfAny(new[] { '\\', '/', '*', '?' }) >= 0)
                problems.Add("The extension cannot contain a path separator or a wildcard.");

            problems.AddRange(ValidateColumns(template.Columns, inherited));
            problems.AddRange(ValidateSort(template, inherited));

            return problems;
        }

        private static IEnumerable<string> ValidateColumns(List<string>? columns, List<string> inherited)
        {
            var declared = columns ?? new List<string>();

            if (declared.Count + inherited.Count == 0 || declared.Concat(inherited).All(string.IsNullOrWhiteSpace))
            {
                yield return "A template needs at least one column.";
                yield break;
            }

            if (declared.Any(string.IsNullOrWhiteSpace))
                yield return "One of the columns has no name.";

            // Inherited names are held apart from the declared ones so that colliding with the
            // base template says so, rather than reporting a duplicate the form is not showing.
            var fromBase = new HashSet<string>(inherited, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in declared.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()))
            {
                if (!seen.Add(column))
                    yield return $"The column \"{column}\" is listed more than once.";
                else if (fromBase.Contains(column))
                    yield return $"\"{column}\" already comes from the inherited template. Remove it here or drop the inherit.";

                if (LogProvenanceColumns.IsProvenance(column))
                    yield return $"\"{column}\" is added to every row automatically — it cannot also be a template column.";

                if (LogOverflowColumns.IsGeneratedName(column))
                    yield return $"\"{column}\" clashes with the extra columns generated for overlong rows. Pick another name.";

                if (column.IndexOfAny(IllegalInColumnName) >= 0)
                    yield return $"\"{column}\" cannot contain [ or ].";
            }
        }

        /// <summary>
        /// Sort rows may name a provenance column — every shipped template ends its sort with
        /// Location, SourceFile, Sequence — so the check is against the declared columns
        /// <em>plus</em> those, not against the declared ones alone.
        /// </summary>
        private static IEnumerable<string> ValidateSort(LogTemplate template, List<string> inherited)
        {
            var sort = template.Sort;
            if (sort is null || sort.Count == 0) yield break;

            var sortable = new HashSet<string>(
                (template.Columns ?? new List<string>())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim())
                    .Concat(inherited)
                    .Concat(LogProvenanceColumns.Visible),
                StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sort)
            {
                var column = entry.Column?.Trim() ?? "";
                if (column.Length == 0)
                {
                    yield return "A sort row has no column picked.";
                    continue;
                }

                if (!sortable.Contains(column))
                    yield return $"Sorting by \"{column}\", which is not one of this template's columns.";

                if (!seen.Add(column))
                    yield return $"\"{column}\" is sorted on twice.";
            }
        }
    }
}
