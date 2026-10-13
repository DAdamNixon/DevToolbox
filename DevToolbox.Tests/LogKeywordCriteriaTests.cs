using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>
/// How the basic filter's rows become search criteria. The gates between rows are the only
/// operators: a comma inside a row is text to find, not a separator.
/// </summary>
public class LogKeywordCriteriaTests
{
    private static LogFilterState Rows(params (string Gate, string Text)[] rows) => new()
    {
        TableName = "logs",
        SqlName = "logs",
        SavedQueryTarget = "logs",
        KeywordRows = rows.Select(r => new LogSearchStateService.KeywordRow { Gate = r.Gate, Text = r.Text }).ToList()
    };

    [Fact]
    public void A_comma_is_part_of_the_phrase()
    {
        var criteria = LogSearchStateService.BuildCriteria(Rows(("AND", "Smith, John")));

        var group = Assert.Single(criteria.Groups);
        Assert.Equal(new[] { "Smith, John" }, group.Terms);
    }

    [Fact]
    public void A_phrase_is_trimmed_but_otherwise_kept_whole()
    {
        var criteria = LogSearchStateService.BuildCriteria(Rows(("AND", "  404,GET , /api  ")));

        Assert.Equal(new[] { "404,GET , /api" }, Assert.Single(criteria.Groups).Terms);
    }

    [Fact]
    public void Each_row_is_its_own_group_with_its_gate()
    {
        var criteria = LogSearchStateService.BuildCriteria(Rows(("AND", "error"), ("OR", "timeout"), ("NOT", "health, check")));

        Assert.Equal(new[] { "AND", "OR", "NOT" }, criteria.Groups.Select(g => g.Gate));
        Assert.Equal(new[] { "error", "timeout", "health, check" }, criteria.Groups.Select(g => Assert.Single(g.Terms)));
    }

    [Fact]
    public void Blank_rows_are_skipped()
    {
        var criteria = LogSearchStateService.BuildCriteria(Rows(("AND", "   "), ("AND", "error"), ("OR", "")));

        Assert.Equal("error", Assert.Single(Assert.Single(criteria.Groups).Terms));
        Assert.True(criteria.HasContent);
    }

    [Fact]
    public void A_row_of_only_commas_is_still_a_search()
    {
        // Under the old comma split this row vanished and the filter matched everything.
        var criteria = LogSearchStateService.BuildCriteria(Rows(("AND", ",")));

        Assert.Equal(",", Assert.Single(Assert.Single(criteria.Groups).Terms));
    }
}
