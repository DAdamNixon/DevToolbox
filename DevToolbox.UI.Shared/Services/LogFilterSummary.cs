using System.Text;
using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services;

/// <summary>
/// Renders the logs filter as one line once it is locked behind a collapsed <c>results</c> table —
/// <c>logs · where checkout, cart AND error · Location: Web01 → 1,234 rows</c>. A pure function so
/// the exact wording can be pinned by tests without a page to render.
/// </summary>
public static class LogFilterSummary
{
    public static string Describe(string tableName, LogSearchCriteria criteria, string? savedQueryLabel, string? tabLabel, int rows)
    {
        var parts = new List<string> { tableName };

        var criteriaText = DescribeCriteria(criteria, savedQueryLabel);
        if (!string.IsNullOrEmpty(criteriaText)) parts.Add(criteriaText);

        if (!string.IsNullOrWhiteSpace(tabLabel)) parts.Add(tabLabel!);

        return $"{string.Join(" · ", parts)} → {rows:N0} row{(rows == 1 ? "" : "s")}";
    }

    private static string DescribeCriteria(LogSearchCriteria criteria, string? savedQueryLabel)
    {
        if (criteria.UseAdvanced)
        {
            if (!string.IsNullOrWhiteSpace(savedQueryLabel))
                return savedQueryLabel!;

            var sql = (criteria.AdvancedExpression ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            return sql.Length > 60 ? sql[..60].TrimEnd() + "…" : sql;
        }

        var groups = criteria.Groups.Where(g => g.Terms.Any(t => !string.IsNullOrWhiteSpace(t))).ToList();
        if (groups.Count == 0) return "";

        var sb = new StringBuilder("where ");
        for (var i = 0; i < groups.Count; i++)
        {
            var terms = string.Join(", ", groups[i].Terms.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (i == 0) sb.Append(terms);
            else sb.Append($" {groups[i].Gate.Trim().ToUpperInvariant()} {terms}");
        }
        return sb.ToString();
    }
}
