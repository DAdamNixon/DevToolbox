using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The lexical rules, which are a deliberate port of the legacy tool's regex. These define what
/// "switched off" has meant in the team's hosts files for years, so they are pinned rather than
/// improved.
/// </summary>
public class HostsTokenizerTests
{
    [Theory]
    // A leading '#' is its own token whether or not a space follows it. Real files carry both
    // forms, sometimes in the same group.
    [InlineData("# 192.0.2.1   host", new[] { "#", "192.0.2.1", "host" })]
    [InlineData("#192.0.2.1   host", new[] { "#", "192.0.2.1", "host" })]
    [InlineData("###192.0.2.1", new[] { "#", "#", "#", "192.0.2.1" })]
    // A '#' inside a run is not a token: the run is greedy.
    [InlineData("abc#def", new[] { "abc#def" })]
    [InlineData("192.0.2.1 a#b", new[] { "192.0.2.1", "a#b" })]
    [InlineData("", new string[0])]
    [InlineData("   \t  ", new string[0])]
    public void Tokenize_matches_the_legacy_rules(string text, string[] expected) =>
        Assert.Equal(expected, HostsTokenizer.Tokenize(text));

    [Theory]
    [InlineData("192.0.2.1 host", true)]
    [InlineData("   192.0.2.1 host", true)]
    [InlineData("# 192.0.2.1 host", false)]
    [InlineData("#192.0.2.1 host", false)]
    [InlineData("   # 192.0.2.1 host", false)]
    // A blank line is neither active nor inactive; it is not content at all.
    [InlineData("", false)]
    [InlineData("    ", false)]
    public void IsActive_reads_the_first_non_whitespace_character(string text, bool expected) =>
        Assert.Equal(expected, HostsTokenizer.IsActive(text));

    [Theory]
    [InlineData("192.0.2.1 host", "192.0.2.1 host")]
    [InlineData("# 192.0.2.1 host", "192.0.2.1 host")]
    [InlineData("#192.0.2.1 host", "192.0.2.1 host")]
    [InlineData("###   192.0.2.1 host", "192.0.2.1 host")]
    [InlineData("   #  192.0.2.1 host", "192.0.2.1 host")]
    // Trailing whitespace is content and must not be trimmed, or a switch would alter the line.
    [InlineData("# 192.0.2.1 host  ", "192.0.2.1 host  ")]
    public void StripComment_yields_what_the_line_would_say_if_enabled(string text, string expected) =>
        Assert.Equal(expected, HostsTokenizer.StripComment(text));

    [Fact]
    public void Comment_then_uncomment_returns_the_original_content()
    {
        const string original = "192.0.2.1   host.example.com";

        Assert.Equal(original, HostsTokenizer.Uncomment(HostsTokenizer.Comment(original)));
    }

    [Fact]
    public void Commenting_twice_does_not_stack_markers()
    {
        var once = HostsTokenizer.Comment("192.0.2.1 host");

        Assert.Equal(once, HostsTokenizer.Comment(once));
        Assert.Equal("# 192.0.2.1 host", once);
    }

    [Fact]
    public void Uncommenting_an_enabled_line_changes_nothing()
    {
        const string enabled = "192.0.2.1 host";

        Assert.Equal(enabled, HostsTokenizer.Uncomment(enabled));
    }

    [Fact]
    public void A_line_of_nothing_but_markers_is_left_alone()
    {
        Assert.Equal("###", HostsTokenizer.Uncomment("###"));
        Assert.Equal("   ", HostsTokenizer.Comment("   "));
    }

    /// <summary>
    /// The property the invariant checker leans on: toggling a line changes only its markers.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.1   host.example.com")]
    [InlineData("#192.0.2.1   host.example.com")]
    [InlineData("# 192.0.2.1  host.example.com  # note")]
    [InlineData("127.0.0.1\t\thost.example.com\t##value:Local")]
    public void Toggling_never_changes_a_lines_content(string line)
    {
        var content = HostsTokenizer.StripComment(line);

        Assert.Equal(content, HostsTokenizer.StripComment(HostsTokenizer.Comment(line)));
        Assert.Equal(content, HostsTokenizer.StripComment(HostsTokenizer.Uncomment(line)));
    }

    /// <summary>
    /// Toggling settles after one pass, so repeatedly switching a group back and forth does not
    /// slowly rewrite the file.
    /// </summary>
    [Fact]
    public void Toggling_is_stable_after_the_first_pass()
    {
        const string original = "#192.0.2.1   host";

        var first = HostsTokenizer.Comment(HostsTokenizer.Uncomment(original));
        var second = HostsTokenizer.Comment(HostsTokenizer.Uncomment(first));

        Assert.Equal("# 192.0.2.1   host", first);
        Assert.Equal(first, second);
    }
}
