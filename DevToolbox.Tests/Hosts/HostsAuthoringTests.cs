using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Adding groups, options and entries.
/// <para>
/// Every mutation here is put through <see cref="HostsInvariantChecker"/> before anything is
/// asserted about it, so a change that touched an existing line fails at the gate rather than being
/// asserted around. That is the same gate the write path uses, which makes these tests a check on
/// the guarantee and not just on the arithmetic.
/// </para>
/// </summary>
public class HostsAuthoringTests
{
    private static readonly NewHostsEntry Db01 = new("203.0.113.80", "db01.example.com");

    private static HostsMutation Verified(HostsDocument document, HostsMutation mutation)
    {
        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);
        return mutation;
    }

    private static HostsMap Reparse(HostsMutation mutation, HostsDialect? dialect = null) =>
        HostsAnnotationParser.Parse(mutation.Document, dialect);

    // ── adding a group ───────────────────────────────────────────────────────

    [Fact]
    public void A_new_group_appears_when_the_file_is_read_back()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [
                new NewHostsOption("Local", HostsSeverityLevel.Normal, [new NewHostsEntry("127.0.0.1", "cache.example.com")]),
                new NewHostsOption("Shared", HostsSeverityLevel.Caution, [new NewHostsEntry("203.0.113.9", "cache.example.com")]),
            ]))));

        var group = Reparse(mutation).Find("Cache");

        Assert.NotNull(group);
        Assert.Equal(["Local", "Shared"], group!.Options.Select(option => option.Name));
        Assert.Equal(HostsSeverityLevel.Caution, group.Find("Shared")!.Severity);
    }

    [Fact]
    public void A_new_group_is_added_switched_off_so_nothing_starts_resolving()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var before = map.ActiveEntries.Select(entry => (entry.Address, Name: string.Join(' ', entry.Hostnames))).ToArray();

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        var after = Reparse(mutation);

        Assert.Equal("off", after.Find("Cache")!.Describe());
        Assert.Equal(
            before,
            after.ActiveEntries.Select(entry => (entry.Address, Name: string.Join(' ', entry.Hostnames))).ToArray());
    }

    /// <summary>
    /// The single most damaging thing found in a real file was a group with nothing closing it,
    /// which then owned every line below it. A tool that writes annotations must not create another.
    /// </summary>
    [Fact]
    public void A_new_group_is_always_closed()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        Assert.Equal(HostsScopeEnd.Clear, Reparse(mutation).Find("Cache")!.EndKind);
    }

    [Fact]
    public void Adding_a_group_leaves_the_groups_that_were_already_there_exactly_as_they_were()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        var after = Reparse(mutation);

        foreach (var original in map.Groups)
        {
            var still = after.Find(original.Name);

            Assert.NotNull(still);
            Assert.Equal(original.Describe(), still!.Describe());
            Assert.Equal(original.Options.Count, still.Options.Count);
        }
    }

    [Fact]
    public void A_group_written_in_an_alternate_dialect_is_read_back_in_that_dialect()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.AltDialect, HostsSamples.AlternateDialect);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Live", HostsSeverityLevel.Danger, [Db01])]))));

        var group = Reparse(mutation, HostsSamples.AlternateDialect).Find("Cache");

        Assert.NotNull(group);
        Assert.Equal(HostsSeverityLevel.Danger, group!.Find("Live")!.Severity);

        // The dialect's own word, not the level's name — a file must never carry vocabulary the
        // dialect did not declare.
        Assert.Contains("#@env:Live:prod", mutation.Document.ToText(), StringComparison.Ordinal);
    }

    // ── appending to a file that does not end with a newline ─────────────────

    [Fact]
    public void Appending_to_a_file_with_no_final_newline_does_not_join_onto_its_last_line()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.NoTrailingNewLine);

        Assert.Empty(document.Lines[^1].NewLine);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        var after = Reparse(mutation);

        // The original last line kept its content and is still its own entry, rather than having a
        // group directive welded onto the end of it.
        Assert.Contains(after.Entries, entry => entry.Hostnames.Contains("tail.example.com"));
        Assert.NotNull(after.Find("Cache"));
    }

    // ── adding an option ─────────────────────────────────────────────────────

    [Fact]
    public void A_new_option_joins_its_group_and_stops_at_the_groups_own_end()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Option("DB Server", new NewHostsOption("Spare", Entries: [Db01]))));

        var after = Reparse(mutation);
        var group = after.Find("DB Server")!;

        Assert.Equal(["Test (db02)", "Live", "Spare"], group.Options.Select(option => option.Name));

        // It landed inside DB Server rather than leaking into the group that follows it.
        Assert.Equal(["web01", "web02"], after.Find("Public Web")!.Options.Select(option => option.Name));
    }

    [Fact]
    public void A_new_option_is_added_switched_off_and_does_not_disturb_the_one_that_is_on()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Option("DB Server", new NewHostsOption("Spare", Entries: [Db01]))));

        var group = Reparse(mutation).Find("DB Server")!;

        Assert.False(group.Find("Spare")!.IsOn);
        Assert.Equal("Test (db02)", Assert.Single(group.ActiveOptions).Name);
    }

    [Fact]
    public void An_option_can_be_added_to_the_last_group_even_though_nothing_closes_it()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Equal(HostsScopeEnd.EndOfFile, map.Find("Intranet")!.EndKind);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Option("Intranet", new NewHostsOption("web03", HostsSeverityLevel.Caution, [Db01]))));

        Assert.Contains(Reparse(mutation).Find("Intranet")!.Options, option => option.Name == "web03");
    }

    [Fact]
    public void An_option_name_already_in_the_group_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<InvalidOperationException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Option("DB Server", new NewHostsOption("Live", Entries: [Db01]))));
    }

    [Fact]
    public void An_option_added_to_a_group_that_does_not_exist_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<KeyNotFoundException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Option("Nowhere", new NewHostsOption("Spare", Entries: [Db01]))));
    }

    // ── adding entries ───────────────────────────────────────────────────────

    [Fact]
    public void Entries_added_to_an_option_that_is_off_are_commented_out()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.False(map.Find("DB Server", "Live")!.IsOn);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Entries("DB Server", "Live", [new("203.0.113.11", "db02.example.com")])));

        var option = Reparse(mutation).Find("DB Server", "Live")!;

        Assert.False(option.IsOn);
        Assert.Equal(3, option.TotalCount);
    }

    /// <summary>
    /// The alternative would leave the option reading "2 of 3" — a partial-state warning raised by
    /// something the developer deliberately did.
    /// </summary>
    [Fact]
    public void Entries_added_to_an_option_that_is_on_are_enabled_so_it_does_not_read_as_half_applied()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.True(map.Find("DB Server", "Test (db02)")!.IsOn);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Entries("DB Server", "Test (db02)", [new("203.0.113.80", "db02.example.com")])));

        var option = Reparse(mutation).Find("DB Server", "Test (db02)")!;

        Assert.False(option.IsPartiallyOn);
        Assert.Equal(3, option.ActiveCount);
        Assert.Equal(3, option.TotalCount);
    }

    /// <summary>
    /// Intranet/web02's scope runs to the end of the file and has nine quarantined lines in it. A
    /// new entry belongs with the option's own body, not after somebody else's stray block.
    /// </summary>
    [Fact]
    public void An_entry_is_placed_with_the_options_own_lines_and_not_after_the_quarantined_ones()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var before = map.Find("Intranet", "web02")!;
        Assert.NotEmpty(before.SuspectLines);

        var mutation = Verified(document, HostsMutator.Add(document, map,
            new HostsAddition.Entries("Intranet", "web02", [new("192.0.2.39", "extra.intranet.example.com")])));

        var inserted = Assert.Single(mutation.Changes).Line;

        Assert.True(inserted <= before.SuspectLines.Min(),
            $"line {inserted} was placed inside the quarantined region starting at {before.SuspectLines.Min()}");

        var after = Reparse(mutation).Find("Intranet", "web02")!;

        Assert.Equal(before.TotalCount + 1, after.TotalCount);
        Assert.Equal(before.SuspectLines.Count, after.SuspectLines.Count);
    }

    [Fact]
    public void An_option_tagged_only_line_by_line_has_no_block_to_add_to()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        var inlineOnly = map.Groups
            .SelectMany(group => group.Options.Select(option => (group, option)))
            .First(pair => pair.option.DirectiveLine == 0);

        Assert.Throws<InvalidOperationException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Entries(inlineOnly.group.Name, inlineOnly.option.Name, [Db01])));
    }

    [Fact]
    public void Adding_no_entries_changes_nothing()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = HostsMutator.Add(document, map, new HostsAddition.Entries("DB Server", "Live", []));

        Assert.True(mutation.IsEmpty);
        Assert.Equal(document.ToText(), mutation.Document.ToText());
    }

    // ── what authoring refuses ───────────────────────────────────────────────

    [Fact]
    public void A_group_with_no_options_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<ArgumentException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Group(new NewHostsGroup("Cache", []))));
    }

    [Fact]
    public void A_group_whose_options_share_a_name_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<ArgumentException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Group(new NewHostsGroup("Cache", [
                new NewHostsOption("Local", Entries: [Db01]),
                new NewHostsOption("local", Entries: [Db01]),
            ]))));
    }

    [Fact]
    public void An_entry_that_is_not_a_valid_entry_is_refused_before_anything_is_written()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<ArgumentException>(() => HostsMutator.Add(document, map,
            new HostsAddition.Entries("DB Server", "Live", [new("not-an-address", "db02.example.com")])));
    }

    // ── the file itself ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(HostsSamples.CrlfBom)]
    [InlineData(HostsSamples.LfNoBom)]
    [InlineData(HostsSamples.MixedEndings)]
    [InlineData(HostsSamples.NoTrailingNewLine)]
    public void Adding_a_group_keeps_the_files_encoding_and_line_endings(string sample)
    {
        var (document, map) = HostsSamples.Parse(sample);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        var written = HostsDocumentCodec.Compose(mutation.Document);
        var reread = HostsDocumentCodec.FromBytes(document.Path, written, document.LastWriteTimeUtc);

        Assert.Equal(document.Preamble, reread.Preamble);
        Assert.Equal(document.Encoding.CodePage, reread.Encoding.CodePage);
        Assert.Equal(document.DefaultNewLine, reread.DefaultNewLine);

        // Every original line still carries the terminator it had, except a missing final one which
        // appending necessarily supplies.
        foreach (var original in document.Lines.SkipLast(1))
        {
            Assert.Equal(original.NewLine, reread.Lines[original.Number - 1].NewLine);
        }
    }

    [Fact]
    public void Adding_a_group_to_an_empty_file_produces_one_that_parses()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.Empty);

        var mutation = Verified(document, HostsMutator.Add(document, map, new HostsAddition.Group(
            new NewHostsGroup("Cache", [new NewHostsOption("Local", Entries: [new("127.0.0.1", "cache.example.com")])]))));

        var after = Reparse(mutation);

        Assert.True(after.HasAnnotations);
        Assert.Equal("Cache", Assert.Single(after.Groups).Name);
    }
}
