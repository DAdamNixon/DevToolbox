using System.Collections.Generic;
using System.Linq;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// What the template editor refuses to save.
/// <para>
/// Each rule here is a failure that used to be possible only by hand-editing YAML and would surface
/// as a broken <em>search</em> rather than a broken form: a duplicate column makes the
/// <c>CREATE TABLE</c> fail, a column called <c>Location</c> collides with the one the ingest adds
/// itself, and a <c>]</c> in a name breaks out of the <c>[column]</c> quoting the SQL uses.
/// </para>
/// </summary>
public class LogTemplateValidatorTests
{
    private static LogTemplate Valid(params string[] columns) => new()
    {
        Name = "Test",
        Extension = ".txt",
        Delimiter = "|",
        Columns = columns.Length > 0 ? columns.ToList() : new List<string> { "DateTime" }
    };

    private static List<string> Check(LogTemplate template, IEnumerable<string>? others = null, IEnumerable<string>? inherited = null) =>
        LogTemplateValidator.Validate(template, others ?? Enumerable.Empty<string>(), inherited);

    [Fact]
    public void An_ordinary_template_has_nothing_wrong_with_it()
    {
        Assert.Empty(Check(Valid("DateTime", "IP", "Account Number")));
    }

    [Fact]
    public void A_template_needs_a_name()
    {
        var problems = Check(new LogTemplate { Name = "  ", Extension = ".txt", Columns = new() { "a" } });
        Assert.Contains(problems, p => p.Contains("name"));
    }

    [Fact]
    public void A_name_another_template_already_has_is_refused_regardless_of_case()
    {
        var template = Valid();
        template.Name = "checkout";

        Assert.Contains(Check(template, others: new[] { "Checkout" }), p => p.Contains("already called"));
    }

    [Fact]
    public void A_name_matching_no_other_template_is_fine()
    {
        var template = Valid();
        template.Name = "Checkout";

        Assert.Empty(Check(template, others: new[] { "EE IIS", "WebsiteBase" }));
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("")]
    [InlineData(".*")]
    [InlineData(@".\txt")]
    public void An_extension_that_is_not_a_plain_dotted_suffix_is_refused(string extension)
    {
        var template = Valid();
        template.Extension = extension;

        Assert.NotEmpty(Check(template));
    }

    [Fact]
    public void A_template_with_no_columns_is_refused()
    {
        var template = Valid();
        template.Columns = new List<string>();

        Assert.Contains(Check(template), p => p.Contains("at least one column"));
    }

    [Fact]
    public void A_template_with_no_columns_of_its_own_but_an_inherited_set_is_allowed()
    {
        var template = Valid();
        template.Columns = new List<string>();
        template.Inherits = "WebsiteBase";

        Assert.Empty(Check(template, inherited: new[] { "DateTime", "Guid" }));
    }

    [Fact]
    public void The_same_column_twice_is_refused()
    {
        Assert.Contains(Check(Valid("DateTime", "IP", "datetime")), p => p.Contains("more than once"));
    }

    [Fact]
    public void A_column_the_ingest_adds_itself_cannot_be_declared()
    {
        foreach (var reserved in LogProvenanceColumns.All)
        {
            Assert.NotEmpty(Check(Valid("DateTime", reserved)));
        }
    }

    [Theory]
    [InlineData("Message1")]
    [InlineData("message1")]
    [InlineData("Message42")]
    // The spaced and underscored forms no longer collide with anything generated, but a template
    // column called "Message 1" sitting beside a generated Message1 is a trap, so it stays refused.
    [InlineData("Message 1")]
    [InlineData("Message_1")]
    public void A_column_named_like_the_generated_overflow_columns_is_refused(string column)
    {
        Assert.Contains(Check(Valid("DateTime", column)), p => p.Contains("overlong rows"));
    }

    [Theory]
    [InlineData("Messages")]
    [InlineData("Message")]
    [InlineData("MessageText")]
    [InlineData("1Message")]
    public void A_column_merely_containing_the_word_Message_is_fine(string column)
    {
        Assert.Empty(Check(Valid("DateTime", column)));
    }

    [Theory]
    [InlineData("cs(User-Agent)")]
    [InlineData("time-taken")]
    [InlineData("X-FORWARDED-FOR")]
    public void The_punctuation_real_IIS_headers_carry_is_allowed(string column)
    {
        Assert.Empty(Check(Valid("date", column)));
    }

    [Theory]
    [InlineData("bad[column")]
    [InlineData("bad]column")]
    public void A_column_name_that_would_break_the_SQL_quoting_is_refused(string column)
    {
        Assert.Contains(Check(Valid("DateTime", column)), p => p.Contains("[ or ]"));
    }

    [Fact]
    public void A_column_that_repeats_an_inherited_one_is_called_out_as_inherited()
    {
        var template = Valid("DateTime", "JobSeq");
        template.Inherits = "WebsiteBase";

        var problems = Check(template, inherited: new[] { "DateTime", "Guid" });

        Assert.Contains(problems, p => p.Contains("inherited template"));
    }

    // --- sort ---

    [Fact]
    public void Sorting_on_the_provenance_columns_is_allowed_even_though_they_are_not_declared()
    {
        var template = Valid("DateTime");
        template.Sort = LogProvenanceColumns.Visible
            .Select(c => new SortColumn { Column = c, Direction = "asc" })
            .ToList();

        Assert.Empty(Check(template));
    }

    [Fact]
    public void Sorting_on_an_inherited_column_is_allowed()
    {
        var template = Valid("JobSeq");
        template.Inherits = "WebsiteBase";
        template.Sort = new List<SortColumn> { new() { Column = "DateTime", Direction = "asc" } };

        Assert.Empty(Check(template, inherited: new[] { "DateTime", "Guid" }));
    }

    [Fact]
    public void Sorting_on_a_column_this_template_does_not_have_is_refused()
    {
        var template = Valid("DateTime");
        template.Sort = new List<SortColumn> { new() { Column = "Nonexistent", Direction = "asc" } };

        Assert.Contains(Check(template), p => p.Contains("Nonexistent"));
    }

    [Fact]
    public void Sorting_twice_on_the_same_column_is_refused()
    {
        var template = Valid("DateTime", "IP");
        template.Sort = new List<SortColumn>
        {
            new() { Column = "DateTime", Direction = "asc" },
            new() { Column = "DateTime", Direction = "desc" }
        };

        Assert.Contains(Check(template), p => p.Contains("sorted on twice"));
    }

    [Fact]
    public void A_sort_row_with_no_column_picked_is_refused()
    {
        var template = Valid("DateTime");
        template.Sort = new List<SortColumn> { new() { Column = "", Direction = "asc" } };

        Assert.Contains(Check(template), p => p.Contains("no column"));
    }

    [Fact]
    public void No_sort_at_all_is_fine()
    {
        var template = Valid("text");
        template.Sort = null;

        Assert.Empty(Check(template));
    }

    [Fact]
    public void An_empty_delimiter_is_fine_because_that_is_how_row_mode_is_configured()
    {
        var template = Valid("text");
        template.Delimiter = "";

        Assert.Empty(Check(template));
    }
}
