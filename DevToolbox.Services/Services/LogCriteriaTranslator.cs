using DevToolbox.Services.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevToolbox.Services.Services
{
    // Translates structured/advanced log search criteria into a parameterized SQL WHERE fragment.
    // User-supplied text is always bound as parameters; only whitelisted operators/columns reach SQL.
    public static class LogCriteriaTranslator
    {
        public static string Build(LogSearchCriteria criteria, IReadOnlyList<string> columns, List<SqliteParameter> parameters)
        {
            if (criteria == null || columns.Count == 0)
                return "";

            int idx = parameters.Count;
            if (criteria.UseAdvanced)
            {
                if (string.IsNullOrWhiteSpace(criteria.AdvancedExpression))
                    return "";
                return BuildAdvanced(criteria.AdvancedExpression!, columns, parameters, ref idx);
            }
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

        // --- Advanced mode: safe boolean DSL over quoted/bare terms with AND / OR / NOT / parentheses. ---

        private static string BuildAdvanced(string expression, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            var tokens = Tokenize(expression);
            if (tokens.Count == 0)
                return "";

            int pos = 0;
            var sql = ParseOr(tokens, ref pos, columns, parameters, ref idx);
            if (pos != tokens.Count)
                throw new ArgumentException($"Invalid advanced query near '{tokens[pos].Text}'.");
            return sql;
        }

        private enum TokType { And, Or, Not, LParen, RParen, Term }

        private readonly struct Tok
        {
            public Tok(TokType type, string text) { Type = type; Text = text; }
            public TokType Type { get; }
            public string Text { get; }
        }

        private static readonly Regex TokenRegex = new(
            "\"[^\"]*\"|\\(|\\)|[^\\s()]+",
            RegexOptions.Compiled);

        private static List<Tok> Tokenize(string expression)
        {
            var tokens = new List<Tok>();
            foreach (Match m in TokenRegex.Matches(expression))
            {
                var raw = m.Value;
                if (raw == "(")
                    tokens.Add(new Tok(TokType.LParen, raw));
                else if (raw == ")")
                    tokens.Add(new Tok(TokType.RParen, raw));
                else if (raw.Length >= 2 && raw.StartsWith("\"") && raw.EndsWith("\""))
                    tokens.Add(new Tok(TokType.Term, raw.Substring(1, raw.Length - 2)));
                else if (string.Equals(raw, "AND", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Tok(TokType.And, raw));
                else if (string.Equals(raw, "OR", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Tok(TokType.Or, raw));
                else if (string.Equals(raw, "NOT", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Tok(TokType.Not, raw));
                else
                    tokens.Add(new Tok(TokType.Term, raw));
            }
            return tokens;
        }

        private static string ParseOr(List<Tok> tokens, ref int pos, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            var left = ParseAnd(tokens, ref pos, columns, parameters, ref idx);
            while (pos < tokens.Count && tokens[pos].Type == TokType.Or)
            {
                pos++;
                var right = ParseAnd(tokens, ref pos, columns, parameters, ref idx);
                left = $"({left} OR {right})";
            }
            return left;
        }

        private static string ParseAnd(List<Tok> tokens, ref int pos, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            var left = ParseNot(tokens, ref pos, columns, parameters, ref idx);
            while (pos < tokens.Count && tokens[pos].Type == TokType.And)
            {
                pos++;
                var right = ParseNot(tokens, ref pos, columns, parameters, ref idx);
                left = $"({left} AND {right})";
            }
            return left;
        }

        private static string ParseNot(List<Tok> tokens, ref int pos, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            if (pos < tokens.Count && tokens[pos].Type == TokType.Not)
            {
                pos++;
                var operand = ParseNot(tokens, ref pos, columns, parameters, ref idx);
                return $"(NOT {operand})";
            }
            return ParsePrimary(tokens, ref pos, columns, parameters, ref idx);
        }

        private static string ParsePrimary(List<Tok> tokens, ref int pos, IReadOnlyList<string> columns, List<SqliteParameter> parameters, ref int idx)
        {
            if (pos >= tokens.Count)
                throw new ArgumentException("Incomplete advanced query.");

            var tok = tokens[pos];
            if (tok.Type == TokType.LParen)
            {
                pos++;
                var inner = ParseOr(tokens, ref pos, columns, parameters, ref idx);
                if (pos >= tokens.Count || tokens[pos].Type != TokType.RParen)
                    throw new ArgumentException("Missing closing parenthesis in advanced query.");
                pos++;
                return inner;
            }
            if (tok.Type == TokType.Term)
            {
                pos++;
                return BuildContains(tok.Text, columns, parameters, ref idx);
            }
            throw new ArgumentException($"Unexpected token '{tok.Text}' in advanced query.");
        }
    }
}
