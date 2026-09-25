using DevToolbox.Services.Models;
using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>The locked logs line's exact wording — pure, so it is pinned without a page.</summary>
public class LogFilterSummaryTests
{
    private static LogSearchCriteria Keyword(params (string Gate, string Text)[] rows)
    {
        var criteria = new LogSearchCriteria();
        foreach (var (gate, text) in rows)
            criteria.Groups.Add(new KeywordGroup { Gate = gate, Terms = text.Split(',').Select(t => t.Trim()).ToList() });
        return criteria;
    }

    [Fact]
    public void A_single_keyword_group()
    {
        var text = LogFilterSummary.Describe("logs", Keyword(("AND", "checkout")), null, null, 1234);
        Assert.Equal("logs · where checkout → 1,234 rows", text);
    }

    [Fact]
    public void Multiple_terms_in_one_group_join_with_a_comma()
    {
        var text = LogFilterSummary.Describe("logs", Keyword(("AND", "checkout, cart")), null, null, 5);
        Assert.Equal("logs · where checkout, cart → 5 rows", text);
    }

    [Fact]
    public void A_second_group_shows_its_gate()
    {
        var text = LogFilterSummary.Describe("logs", Keyword(("AND", "checkout, cart"), ("AND", "error")), null, null, 2);
        Assert.Equal("logs · where checkout, cart AND error → 2 rows", text);
    }

    [Fact]
    public void A_NOT_gate_is_shown()
    {
        var text = LogFilterSummary.Describe("logs", Keyword(("AND", "checkout"), ("NOT", "timeout")), null, null, 9);
        Assert.Equal("logs · where checkout NOT timeout → 9 rows", text);
    }

    [Fact]
    public void A_tab_is_appended()
    {
        var text = LogFilterSummary.Describe("logs", Keyword(("AND", "checkout")), null, "Location: Web01", 1);
        Assert.Equal("logs · where checkout · Location: Web01 → 1 row", text);
    }

    [Fact]
    public void SQL_mode_shows_the_saved_query_label_when_there_is_one()
    {
        var criteria = new LogSearchCriteria { UseAdvanced = true, AdvancedExpression = "SELECT * FROM logs" };
        var text = LogFilterSummary.Describe("logs", criteria, "Checkout / Orders by hour", null, 40);
        Assert.Equal("logs · Checkout / Orders by hour → 40 rows", text);
    }

    [Fact]
    public void SQL_mode_truncates_long_SQL_to_about_60_characters()
    {
        var sql = "SELECT " + string.Join(", ", Enumerable.Range(1, 20).Select(i => $"Col{i}")) + " FROM logs";
        var criteria = new LogSearchCriteria { UseAdvanced = true, AdvancedExpression = sql };

        var text = LogFilterSummary.Describe("logs", criteria, null, null, 3);

        Assert.StartsWith("logs · SELECT Col1, Col2", text);
        Assert.Contains("…", text);
        Assert.EndsWith("→ 3 rows", text);
    }

    [Fact]
    public void SQL_mode_collapses_newlines_onto_one_line()
    {
        var criteria = new LogSearchCriteria { UseAdvanced = true, AdvancedExpression = "SELECT *\nFROM logs" };
        var text = LogFilterSummary.Describe("logs", criteria, null, null, 1);
        Assert.Equal("logs · SELECT * FROM logs → 1 row", text);
    }
}
