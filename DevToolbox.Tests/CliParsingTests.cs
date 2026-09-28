using DevToolbox.Cli;

namespace DevToolbox.Tests;

/// <summary>The command line's grammar: what parses, what is refused before anything runs.</summary>
public class CliParsingTests
{
    private static System.CommandLine.ParseResult Parse(params string[] args) => Cli.Cli.Build().Parse(args);

    [Fact]
    public void A_search_takes_several_locations_and_a_date_range()
    {
        var result = Parse("logs", "search", "app", "-l", "Sample logs", "-l", "System logs", "--from", "2026-09-20", "--to", "2026-09-25", "--terms", "Timeout", "ERROR");

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "Sample logs", "System logs" }, result.GetValue<string[]>("--location"));
        Assert.Equal(new DateOnly(2026, 9, 20), result.GetValue<DateOnly?>("--from"));
        Assert.Equal(new[] { "Timeout", "ERROR" }, result.GetValue<string[]>("--terms"));
    }

    [Fact]
    public void A_date_that_is_not_a_date_is_refused_before_anything_is_read()
    {
        Assert.NotEmpty(Parse("logs", "search", "app", "--from", "last tuesday").Errors);
    }

    [Fact]
    public void Open_only_knows_its_four_ways_of_opening()
    {
        Assert.Empty(Parse("projects", "open", "DevToolbox", "--in", "terminal").Errors);
        Assert.NotEmpty(Parse("projects", "open", "DevToolbox", "--in", "notepad").Errors);
    }

    [Fact]
    public void Projects_lists_everything_when_no_query_is_given()
    {
        var result = Parse("projects");

        Assert.Empty(result.Errors);
        Assert.Null(result.GetValue<string?>("query"));
    }

    [Theory]
    [InlineData("projects", true)]
    [InlineData("logs", true)]
    [InlineData("--quit", false)]
    [InlineData("--no-window", false)]
    public void Only_the_command_words_leave_the_window_alone(string first, bool isCommandLine)
    {
        // The Linux host runs the command line for these and the app for everything else.
        Assert.Equal(isCommandLine, Cli.Cli.Verbs.Contains(first));
    }
}
