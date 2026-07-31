using DevToolbox.Services.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DevToolbox.Services.Services
{
    // Translates the keyword-group search into a parameterized SQL WHERE fragment.
    // User-supplied terms are always bound as parameters; only whitelisted columns reach SQL.
    public static class LogCriteriaTranslator
    {
        public static string Build(LogSearchCriteria criteria, IReadOnlyList<string> columns, List<SqliteParameter> parameters)
        {
            if (criteria == null || columns.Count == 0)
                return "";

            int idx = parameters.Count;
            return BuildGroups(criteria.Groups, columns, parameters, ref idx);
        }

        private static string BuildGroups(List<KeywordGroup> groups, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            string predicate = "";
            foreach (var group in groups)
            {
                var gp = BuildGroupPredicate(group, columns, parameters, ref idx);
                if (string.IsNullOrEmpty(gp))
                    continue;

                if (string.IsNullOrEmpty(predicate))
                    predicate = IsGate(group.Gate, "NOT") ? $"(NOT {gp})" : gp;
                else if (IsGate(group.Gate, "OR"))
                    predicate = $"({predicate} OR {gp})";
                else if (IsGate(group.Gate, "NOT"))
                    predicate = $"({predicate} AND NOT {gp})";
                else
                    predicate = $"({predicate} AND {gp})";
            }
            return predicate;
        }

        private static bool IsGate(string? gate, string value) =>
            string.Equals(gate?.Trim(), value, StringComparison.OrdinalIgnoreCase);

        private static string BuildGroupPredicate(KeywordGroup group, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            var termClauses = new List<string>();
            foreach (var term in group.Terms)
            {
                if (string.IsNullOrWhiteSpace(term))
                    continue;
                termClauses.Add(BuildContains(term, columns, parameters, ref idx));
            }

            return termClauses.Count == 0 ? "" : "(" + string.Join(" OR ", termClauses) + ")";
        }

        // A single "contains" term: matches when the term appears in ANY column.
        private static string BuildContains(string term, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            var pName = $"@c{idx++}";
            parameters.Add(new SqliteParameter(pName, $"%{term}%"));
            var colClauses = columns.Select(c => $"[{c}] LIKE {pName}");
            return "(" + string.Join(" OR ", colClauses) + ")";
        }
    }
}
