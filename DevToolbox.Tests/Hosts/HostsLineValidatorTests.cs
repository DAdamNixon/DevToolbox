using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// What authoring refuses to write, and what it composes when it agrees to.
/// <para>
/// Two kinds of rule are being checked. The format's own — an address that parses, a name that
/// could resolve — and the ones that exist so a written line parses back as what was meant. The
/// second kind is the interesting one: it is the difference between a file this tool can read and
/// a file it can read as the same thing it just wrote.
/// </para>
/// </summary>
public class HostsLineValidatorTests
{
    private static readonly HostsDialect Dialect = HostsDialect.Default;

    private static NewHostsEntry Entry(string address, string hostnames, string? comment = null) =>
        new(address, hostnames, comment);

    // ── addresses ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("203.0.113.80")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void A_canonical_address_is_accepted(string address) =>
        Assert.Empty(HostsLineValidator.ValidateEntry(Entry(address, "db01.example.com"), Dialect));

    [Fact]
    public void An_address_that_is_not_one_is_refused() =>
        Assert.NotEmpty(HostsLineValidator.ValidateEntry(Entry("db01", "db01.example.com"), Dialect));

    [Fact]
    public void A_missing_address_is_refused() =>
        Assert.NotEmpty(HostsLineValidator.ValidateEntry(Entry("   ", "db01.example.com"), Dialect));

    /// <summary>
    /// TryParse accepts far more than anybody writing a hosts file expects: "1" is a valid address
    /// meaning 0.0.0.1, and a zero-padded octet is read as octal-looking but parsed as decimal. Both
    /// would sit in the file looking like one thing and resolving as another.
    /// </summary>
    [Theory]
    [InlineData("1", "0.0.0.1")]
    [InlineData("10.1.2.03", "10.1.2.3")]
    [InlineData("192.168.1", "192.168.0.1")]
    public void An_address_that_means_something_else_is_refused_and_says_what_it_means(string typed, string means)
    {
        var problems = HostsLineValidator.ValidateEntry(Entry(typed, "db01.example.com"), Dialect);

        Assert.Contains(problems, problem => problem.Contains(means, StringComparison.Ordinal));
    }

    // ── hostnames ────────────────────────────────────────────────────────────

    [Fact]
    public void At_least_one_hostname_is_required() =>
        Assert.NotEmpty(HostsLineValidator.ValidateEntry(Entry("127.0.0.1", "  "), Dialect));

    [Fact]
    public void Several_hostnames_on_one_line_are_accepted() =>
        Assert.Empty(HostsLineValidator.ValidateEntry(
            Entry("127.0.0.1", "db01.example.com  db01\tdb"), Dialect));

    [Theory]
    [InlineData("db01.example.com")]
    [InlineData("db-01")]
    [InlineData("build_server")]
    public void A_usable_hostname_is_accepted(string name) => Assert.Null(HostsLineValidator.HostnameProblem(name));

    [Theory]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("exa mple.com")]
    [InlineData("http://example.com")]
    public void An_unusable_hostname_is_refused(string name) => Assert.NotNull(HostsLineValidator.HostnameProblem(name));

    [Fact]
    public void A_label_longer_than_the_limit_is_refused() =>
        Assert.NotNull(HostsLineValidator.HostnameProblem(new string('a', HostsLineValidator.MaxLabelLength + 1)));

    // ── names, and the round trip they have to survive ───────────────────────

    [Fact]
    public void A_plain_name_is_accepted() => Assert.Empty(HostsLineValidator.ValidateName("DB Server", Dialect, "group"));

    [Fact]
    public void An_empty_name_is_refused() => Assert.NotEmpty(HostsLineValidator.ValidateName("  ", Dialect, "group"));

    [Fact]
    public void A_name_containing_the_directive_prefix_is_refused() =>
        Assert.NotEmpty(HostsLineValidator.ValidateName("DB##Server", Dialect, "group"));

    /// <summary>
    /// The reason this rule exists, demonstrated rather than asserted in the abstract: an option
    /// called "Live:warn" would be written as <c>##value:Live:warn</c>, which parses back as an
    /// option called "Live" carrying a danger flag. Reading such a file is supported; writing one
    /// that means something else on the way back in is not.
    /// </summary>
    [Fact]
    public void A_name_ending_in_a_flag_word_would_not_survive_the_round_trip_so_it_is_refused()
    {
        Assert.NotEmpty(HostsLineValidator.ValidateName("Live:warn", Dialect, "option"));

        var (_, map) = HostsSamples.ParseText("##key:G\n##value:Live:warn\n127.0.0.1 a.example.com\n##clear\n");
        var option = Assert.Single(map.Find("G")!.Options);

        Assert.Equal("Live", option.Name);
        Assert.Equal(HostsSeverityLevel.Danger, option.Severity);
    }

    [Fact]
    public void A_note_cannot_smuggle_in_a_directive() =>
        Assert.NotEmpty(HostsLineValidator.ValidateEntry(
            Entry("127.0.0.1", "a.example.com", "see ##value:Other"), Dialect));

    // ── composing ────────────────────────────────────────────────────────────

    [Fact]
    public void An_entry_is_composed_with_its_names_and_note()
    {
        var line = HostsLineValidator.ComposeEntry(Entry("203.0.113.80", "db01.example.com   db01", "primary"));

        Assert.StartsWith("203.0.113.80", line, StringComparison.Ordinal);
        Assert.Contains("db01.example.com db01", line, StringComparison.Ordinal);
        Assert.EndsWith("# primary", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composed_entry_is_an_enabled_line_that_parses_back_to_what_was_typed()
    {
        var line = HostsLineValidator.ComposeEntry(Entry("203.0.113.80", "db01.example.com", "primary"));

        Assert.True(HostsTokenizer.IsActive(line));

        var (_, map) = HostsSamples.ParseText($"##key:G\n##value:O\n{line}\n##clear\n");
        var entry = Assert.Single(map.ActiveEntries);

        Assert.Equal("203.0.113.80", entry.Address);
        Assert.Equal(["db01.example.com"], entry.Hostnames);

        // The note is behind a comment marker, so it is a note and not a third hostname — which is
        // exactly the defect found in a real file, where bracketed text after the names would have
        // become extra hostnames the moment the line was enabled.
        Assert.Null(entry.TrailingText);
    }

    // ── taking a line back apart ─────────────────────────────────────────────

    [Theory]
    [InlineData("203.0.113.80", "db01.example.com", null)]
    [InlineData("203.0.113.80", "db01.example.com db01", "primary")]
    [InlineData("::1", "local.example.com", "v6")]
    public void Composing_and_decomposing_an_entry_round_trips(string address, string names, string? note)
    {
        var original = Entry(address, names, note);
        var back = HostsLineValidator.DecomposeEntry(HostsLineValidator.ComposeEntry(original));

        Assert.Equal(original, back);
    }

    [Fact]
    public void A_commented_line_decomposes_just_like_a_live_one() =>
        Assert.Equal(
            Entry("203.0.113.80", "db01.example.com"),
            HostsLineValidator.DecomposeEntry("#   203.0.113.80    db01.example.com"));

    /// <summary>
    /// Bracketed text after the hostnames is not a comment in the hosts format — it becomes extra
    /// hostnames the moment the line is enabled. Pulling it into the note means that editing such a
    /// line through the dialog quietly repairs it.
    /// </summary>
    [Fact]
    public void Bracketed_text_after_the_hostnames_becomes_part_of_the_note()
    {
        var entry = HostsLineValidator.DecomposeEntry("# 192.0.2.143   www.example.com (all traffic)");

        Assert.NotNull(entry);
        Assert.Equal("www.example.com", entry!.Hostnames);
        Assert.Equal("(all traffic)", entry.Comment);

        var repaired = HostsLineValidator.ComposeEntry(entry);
        Assert.Empty(HostsLineValidator.ValidateEntry(entry, Dialect));
        Assert.Contains("# (all traffic)", repaired, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Added by Docker Desktop")]
    [InlineData("203.0.113.80")]
    public void A_line_that_is_not_an_entry_does_not_decompose(string text) =>
        Assert.Null(HostsLineValidator.DecomposeEntry(text));

    [Fact]
    public void A_composed_entry_with_no_note_carries_no_comment_marker() =>
        Assert.DoesNotContain('#', HostsLineValidator.ComposeEntry(Entry("127.0.0.1", "a.example.com")));
}
