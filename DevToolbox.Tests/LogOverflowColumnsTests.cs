using System.Linq;
using DevToolbox.Services.Models;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// Naming of the columns generated for fields a line carries beyond its template.
/// <para>
/// They used to be called <c>Message 1</c>. The space meant every hand-written query in the advanced
/// SQL box had to spell it <c>[Message 1]</c>, and forgetting the brackets is a syntax error rather
/// than a hint — so the name is now <c>Message1</c>. That property, "usable in SQL without quoting",
/// is what these tests hold on to; the exact spelling is secondary to it.
/// </para>
/// </summary>
public class LogOverflowColumnsTests
{
    [Fact]
    public void The_first_overflow_field_is_Message1()
    {
        Assert.Equal("Message1", LogOverflowColumns.Name(1));
        Assert.Equal("Message2", LogOverflowColumns.Name(2));
        Assert.Equal("Message10", LogOverflowColumns.Name(10));
    }

    [Fact]
    public void A_generated_name_needs_no_quoting_to_be_used_in_a_query()
    {
        // The whole point of the change: a bare identifier — letters and digits, no leading digit,
        // nothing SQLite would need [brackets] around.
        foreach (var name in Enumerable.Range(1, 30).Select(LogOverflowColumns.Name))
        {
            Assert.DoesNotContain(' ', name);
            Assert.True(char.IsLetter(name[0]), $"{name} must not start with a digit");
            Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c), $"{name} has punctuation in it"));
        }
    }

    [Theory]
    [InlineData("Message1", true)]
    [InlineData("message7", true)]
    [InlineData("MESSAGE12", true)]
    // Recognised too, so a template cannot declare one and sit confusingly beside the generated set.
    [InlineData("Message 1", true)]
    [InlineData("Message_1", true)]
    [InlineData("  Message1  ", true)]
    [InlineData("Message", false)]
    [InlineData("Messages", false)]
    [InlineData("MessageText", false)]
    [InlineData("Message1a", false)]
    [InlineData("1Message", false)]
    [InlineData("", false)]
    public void Generated_names_are_recognised_and_ordinary_ones_are_not(string column, bool expected)
    {
        Assert.Equal(expected, LogOverflowColumns.IsGeneratedName(column));
    }

    [Fact]
    public void A_null_name_is_not_a_generated_one()
    {
        Assert.False(LogOverflowColumns.IsGeneratedName(null));
    }

    [Fact]
    public void A_generated_name_is_not_mistaken_for_a_provenance_column()
    {
        // Both sets are appended by the ingest and both are refused as template columns, but they are
        // checked separately — an overlap would make one of the two checks dead code.
        foreach (var name in Enumerable.Range(1, 5).Select(LogOverflowColumns.Name))
        {
            Assert.False(LogProvenanceColumns.IsProvenance(name));
        }

        foreach (var name in LogProvenanceColumns.All)
        {
            Assert.False(LogOverflowColumns.IsGeneratedName(name));
        }
    }
}
