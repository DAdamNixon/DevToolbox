using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Copying an option — the "same hostnames, different address" case.
/// <para>
/// The whole value is that the names come across by construction, so the tests are mostly about
/// exactly that: what the copy points at may change, what it is called may change, and the list of
/// names may not.
/// </para>
/// </summary>
public class HostsOptionCopyTests
{
    private static (HostsDocument Document, HostsMap Map) Sample() => HostsSamples.Parse(HostsSamples.CrlfBom);

    private static string[] NamesOf(HostsMap map, string group, string option) =>
        map.Entries
            .Where(entry => entry.Group == group && entry.Option == option && !entry.IsSuspect)
            .SelectMany(entry => entry.Hostnames)
            .ToArray();

    [Fact]
    public void A_copy_keeps_every_hostname_and_takes_the_new_address()
    {
        var (document, map) = Sample();
        var source = map.Find("Local Sites", "Me")!;

        var copy = HostsOptionCopy.From(document, source, "Mine", "198.51.100.7");

        Assert.Equal("Mine", copy.Name);
        Assert.Equal(source.OwnedLines.Count, copy.EntryList.Count);
        Assert.All(copy.EntryList, entry => Assert.Equal("198.51.100.7", entry.Address));

        Assert.Equal(
            NamesOf(map, "Local Sites", "Me"),
            copy.EntryList.SelectMany(entry => HostsLineValidator.SplitHostnames(entry.Hostnames)));
    }

    [Fact]
    public void Leaving_the_address_out_duplicates_the_option_as_it_stands()
    {
        var (document, map) = Sample();
        var source = map.Find("DB Server", "Test (db02)")!;

        var copy = HostsOptionCopy.From(document, source, "Spare");

        Assert.Equal(["203.0.113.80", "203.0.113.80"], copy.EntryList.Select(entry => entry.Address));
    }

    [Fact]
    public void A_copy_keeps_the_source_flag_unless_told_otherwise()
    {
        var (document, map) = Sample();
        var source = map.Find("DB Server", "Live")!;

        Assert.Equal(HostsSeverityLevel.Danger, HostsOptionCopy.From(document, source, "Spare").Severity);
        Assert.Equal(
            HostsSeverityLevel.Normal,
            HostsOptionCopy.From(document, source, "Spare", severity: HostsSeverityLevel.Normal).Severity);
    }

    /// <summary>
    /// The point of the whole feature, end to end: copy an option, give it one new address, add it,
    /// and read the file back.
    /// </summary>
    [Fact]
    public void A_copied_option_added_to_the_file_resolves_the_same_names_somewhere_else()
    {
        var (document, map) = Sample();
        var source = map.Find("Local Sites", "Me")!;

        var addition = new HostsAddition.Option(
            "Local Sites", HostsOptionCopy.From(document, source, "Mine", "198.51.100.7"));

        var mutation = HostsMutator.Add(document, map, addition);
        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);

        var after = HostsAnnotationParser.Parse(mutation.Document);

        Assert.Equal(
            NamesOf(map, "Local Sites", "Me"),
            NamesOf(after, "Local Sites", "Mine"));

        // Added switched off, so copying one changes nothing about what the machine resolves — the
        // copy is a choice you can now make, not one that has been made for you.
        Assert.False(after.Find("Local Sites", "Mine")!.IsOn);
        Assert.Equal("Me", Assert.Single(after.Find("Local Sites")!.ActiveOptions).Name);
    }

    [Fact]
    public void Copying_the_option_that_is_on_does_not_disturb_it()
    {
        var (document, map) = Sample();
        var source = map.Find("Local Sites", "Me")!;

        var mutation = HostsMutator.Add(document, map, new HostsAddition.Option(
            "Local Sites", HostsOptionCopy.From(document, source, "Mine", "198.51.100.7")));

        var after = HostsAnnotationParser.Parse(mutation.Document);
        var original = after.Find("Local Sites", "Me")!;

        Assert.Equal(source.ActiveCount, original.ActiveCount);
        Assert.Equal(source.TotalCount, original.TotalCount);
    }

    /// <summary>
    /// The copy is composed from parsed entries, so bracketed text after the hostnames comes out as
    /// a real comment rather than being carried across to become extra hostnames.
    /// </summary>
    [Fact]
    public void A_copy_does_not_carry_bracketed_text_across_as_hostnames()
    {
        var (document, map) = Sample();
        var source = map.Find("Public Web", "web01")!;

        var copy = HostsOptionCopy.From(document, source, "web03", "192.0.2.9");

        Assert.Contains(copy.EntryList, entry => entry.Comment is not null);
        Assert.All(copy.EntryList, entry =>
            Assert.DoesNotContain('(', entry.Hostnames));

        // And the result is a set of entries the validator is happy to write.
        Assert.All(copy.EntryList, entry =>
            Assert.Empty(HostsLineValidator.ValidateEntry(entry, map.Dialect)));
    }

    // ── the address box ──────────────────────────────────────────────────────

    [Fact]
    public void An_option_pointing_everywhere_at_one_address_reports_it()
    {
        var (document, map) = Sample();

        Assert.Equal("127.0.0.1", HostsOptionCopy.SharedAddress(document, map.Find("Local Sites", "Me")!));
        Assert.Equal("203.0.113.11", HostsOptionCopy.SharedAddress(document, map.Find("DB Server", "Live")!));
    }

    [Fact]
    public void An_option_whose_entries_disagree_reports_no_shared_address()
    {
        var (document, map) = HostsSamples.ParseText(
            """
            ##key:G
            ##value:Mixed
            127.0.0.1 a.example.com
            203.0.113.9 b.example.com
            ##clear

            """);

        Assert.Null(HostsOptionCopy.SharedAddress(document, map.Find("G", "Mixed")!));
    }

    [Fact]
    public void An_option_with_nothing_in_it_copies_to_nothing_rather_than_failing()
    {
        var (document, map) = HostsSamples.ParseText(
            """
            ##key:G
            ##value:Empty
            ##clear

            """);

        var source = map.Find("G", "Empty")!;

        Assert.Empty(HostsOptionCopy.EntriesOf(document, source));
        Assert.Null(HostsOptionCopy.SharedAddress(document, source));
    }
}
