using System;
using System.Collections.Generic;
using System.Text;

namespace DevToolbox.Services.Services;

/// <summary>
/// Points the table names a user writes in SQL mode — <c>logs</c>, <c>results</c> — at the tables a
/// Log Viewer really queries, which are named per DI scope (see <see cref="DbLogService.ForScope"/>).
/// <para>
/// A rewrite of the text, not a temporary view called <c>logs</c>: a view has no <c>rowid</c>, and
/// <c>ORDER BY rowid</c> is how a query gets rows back in the order they were read. Saved queries and
/// the SQL box both say <c>FROM logs</c>, and both have to go on meaning this window's table.
/// </para>
/// <para>
/// Only a name in table position is replaced — straight after FROM or JOIN, or after a comma in a
/// FROM list — so a column called <c>Results</c>, a string <c>'logs'</c> or a comment is left alone.
/// It is replaced by the real table aliased back to what was written
/// (<c>FROM [logs_3f9a…] AS logs</c>), so <c>logs.Message</c> still resolves, and the column headings
/// SQLite derives from the text still say <c>logs</c>. A reference the user already aliased keeps
/// their alias.
/// </para>
/// </summary>
public static class LogSqlTableNames
{
    /// <summary>Clause keywords that end a FROM list at their own depth: a comma after one is not a join.</summary>
    private static readonly HashSet<string> FromEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT",
        "UNION", "EXCEPT", "INTERSECT", "SELECT", "VALUES", "RETURNING"
    };

    /// <summary>
    /// What can follow a table in a FROM list other than an alias. Any other bare word there is an
    /// alias the user gave it, and adding <c>AS</c> as well would be a syntax error.
    /// </summary>
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "UNION", "EXCEPT", "INTERSECT",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "ON", "USING",
        "INDEXED", "NOT", "RETURNING", "SELECT", "VALUES"
    };

    /// <summary>
    /// <paramref name="sql"/> with every table-position reference to a key of
    /// <paramref name="tables"/> pointed at its value. Keys match the way SQLite matches identifiers
    /// — bare or quoted, any case. Values must be plain identifiers; they are written as <c>[name]</c>.
    /// Returns <paramref name="sql"/> itself when no name actually changes.
    /// </summary>
    public static string Resolve(string sql, IReadOnlyDictionary<string, string> tables)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (written, actual) in tables)
            if (!string.Equals(written, actual, StringComparison.OrdinalIgnoreCase))
                renames[written] = actual;
        if (renames.Count == 0 || string.IsNullOrEmpty(sql)) return sql;

        var tokens = Tokenize(sql);
        var edits = new List<(int Start, int Length, string Text)>();

        // One flag of each per open parenthesis: is a comma at this depth a join, or between two CTEs?
        var inFrom = new List<bool> { false };
        var inWith = new List<bool> { false };

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var previous = i > 0 ? tokens[i - 1] : default;

            if (token.Is("(")) { inFrom.Add(false); inWith.Add(false); continue; }
            if (token.Is(")"))
            {
                if (inFrom.Count > 1) { inFrom.RemoveAt(inFrom.Count - 1); inWith.RemoveAt(inWith.Count - 1); }
                continue;
            }

            if (token.Kind == TokenKind.Word)
            {
                if (token.IsWord("FROM"))
                {
                    if (!IsDistinctFrom(tokens, i)) inFrom[^1] = true;
                    continue;
                }
                if (token.IsWord("WITH")) { inWith[^1] = true; continue; }
                if (FromEnders.Contains(token.Text)) { inFrom[^1] = false; inWith[^1] = false; continue; }
            }

            if (token.Kind is not (TokenKind.Word or TokenKind.Quoted)) continue;

            // A CTE the query names logs is the query's own, and every later mention means it.
            if (inWith[^1] && (previous.IsWord("WITH") || previous.IsWord("RECURSIVE") || previous.Is(",")))
            {
                renames.Remove(token.Name);
                continue;
            }

            if (!renames.TryGetValue(token.Name, out var actual)) continue;

            var inTablePosition =
                (previous.IsWord("FROM") && !IsDistinctFrom(tokens, i - 1)) ||
                previous.IsWord("JOIN") ||
                (previous.Is(",") && inFrom[^1]);
            if (!inTablePosition) continue;

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;

            // logs.something would make it a schema name, which no table here is.
            if (next.Is(".")) continue;

            var aliased = next.Kind == TokenKind.Quoted || (next.Kind == TokenKind.Word && !NotAnAlias.Contains(next.Text));
            var replacement = aliased ? $"[{actual}]" : $"[{actual}] AS {token.Text}";
            edits.Add((token.Start, token.Text.Length, replacement));
        }

        if (edits.Count == 0) return sql;

        var sb = new StringBuilder(sql.Length + edits.Count * 32);
        var at = 0;
        foreach (var (start, length, text) in edits)
        {
            sb.Append(sql, at, start - at).Append(text);
            at = start + length;
        }
        return sb.Append(sql, at, sql.Length - at).ToString();
    }

    /// <summary>IS [NOT] DISTINCT FROM compares two values; that FROM does not start a FROM list.</summary>
    private static bool IsDistinctFrom(List<Token> tokens, int fromIndex) =>
        fromIndex > 0 && tokens[fromIndex - 1].IsWord("DISTINCT");

    private enum TokenKind { None, Word, Quoted, Other }

    /// <param name="Name">For a quoted identifier, what it names: quotes off, doubled quotes undone.</param>
    private readonly record struct Token(TokenKind Kind, int Start, string Text, string Name)
    {
        public bool Is(string punctuation) => Kind == TokenKind.Other && Text == punctuation;
        public bool IsWord(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SQLite's lexical structure, as far as telling identifiers from everything else needs: strings,
    /// the three identifier quotes, comments and parameters are each one token, so nothing inside them
    /// is ever mistaken for a table name. Whitespace and comments are dropped.
    /// </summary>
    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var start = i;

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && At(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && At(sql, i + 1) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c);
                var text = sql[start..i];
                tokens.Add(c == '\''
                    ? new Token(TokenKind.Other, start, text, "")
                    : new Token(TokenKind.Quoted, start, text, Unquote(text, c)));
                continue;
            }

            if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                i = end < 0 ? sql.Length : end + 1;
                var text = sql[start..i];
                tokens.Add(new Token(TokenKind.Quoted, start, text, text.Length >= 2 && text[^1] == ']' ? text[1..^1] : text[1..]));
                continue;
            }

            if (IsWordStart(c))
            {
                while (i < sql.Length && IsWordPart(sql[i])) i++;
                var text = sql[start..i];
                tokens.Add(new Token(TokenKind.Word, start, text, text));
                continue;
            }

            // Numbers and parameters (?1, :name, @name, $name) run to the end of their word, so a
            // parameter called @logs stays a parameter.
            if (char.IsDigit(c) || (c is '?' or ':' or '@' or '$') || (c == '.' && char.IsDigit(At(sql, i + 1))))
            {
                i++;
                while (i < sql.Length && (IsWordPart(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Other, start, sql[start..i], ""));
                continue;
            }

            i++;
            tokens.Add(new Token(TokenKind.Other, start, c.ToString(), ""));
        }
        return tokens;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c > '\u007f';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c > '\u007f';

    /// <summary>The index just past a quoted run starting at <paramref name="i"/>; a doubled quote is part of it.</summary>
    private static int SkipQuoted(string sql, int i, char quote)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (At(sql, i + 1) == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static string Unquote(string text, char quote)
    {
        var inner = text.Length >= 2 && text[^1] == quote ? text[1..^1] : text[1..];
        return inner.Replace(new string(quote, 2), quote.ToString());
    }
}
