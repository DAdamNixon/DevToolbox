using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Renaming, rewriting and removing what is already in the file.
/// <para>
/// Every mutation goes through <see cref="HostsInvariantChecker"/> before anything is asserted, so
/// a change set that does not describe its own result fails at the gate. That is the guarantee
/// editing trades for: it may change content, but it can never change more than it said it would.
/// </para>
/// </summary>
public class HostsEditingTests
{
    private static HostsMutation Edit(HostsDocument document, HostsMap map, HostsEdit edit)
    {
        var mutation = HostsEditor.Apply(document, map, edit);
        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);

        return mutation;
    }

    private static HostsMap Reparse(HostsMutation mutation, HostsDialect? dialect = null) =>
        HostsAnnotationParser.Parse(mutation.Document, dialect);

    // ── renaming a group ─────────────────────────────────────────────────────

    [Fact]
    public void Renaming_a_group_keeps_its_options_and_what_is_switched_on()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var before = map.Find("DB Server")!;

        var mutation = Edit(document, map, new HostsEdit.RenameGroup("DB Server", "Database"));
        var after = Reparse(mutation);

        Assert.Null(after.Find("DB Server"));

        var renamed = after.Find("Database")!;
        Assert.Equal(before.Options.Select(o => o.Name), renamed.Options.Select(o => o.Name));
        Assert.Equal(before.Describe(), renamed.Describe());
    }

    [Fact]
    public void Renaming_a_group_changes_one_line_and_leaves_its_entries_alone()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map, new HostsEdit.RenameGroup("DB Server", "Database"));
        var change = Assert.Single(mutation.Changes);

        Assert.Equal(HostsLineChangeKind.Edited, change.Kind);
        Assert.Equal(map.Find("DB Server")!.DirectiveLine, change.Line);
        Assert.Equal(document.Lines.Count, mutation.Document.Lines.Count);
    }

    /// <summary>
    /// A group directive repeated later reopens the same group, so renaming one occurrence and not
    /// the other would split one group into two.
    /// </summary>
    [Fact]
    public void Renaming_a_group_rewrites_every_directive_that_opens_it()
    {
        var (document, map) = HostsSamples.ParseText(
            """
            ##key:Db
            ##value:Test
            127.0.0.1 a.example.com
            ##clear
            ##key:Db
            ##value:Live
            # 203.0.113.9 a.example.com
            ##clear

            """);

        Assert.Equal(2, map.Find("Db")!.DirectiveLines.Count);

        var mutation = Edit(document, map, new HostsEdit.RenameGroup("Db", "Database"));
        var after = Reparse(mutation);

        Assert.Equal(2, mutation.Changes.Count);
        Assert.Null(after.Find("Db"));
        Assert.Equal(["Test", "Live"], after.Find("Database")!.Options.Select(option => option.Name));
    }

    /// <summary>Directives may be indented, and a rename must not quietly straighten the file out.</summary>
    [Fact]
    public void Renaming_keeps_whatever_preceded_the_directive()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.IndentedDirectives);
        var group = map.Groups[0];
        var original = document.Lines[group.DirectiveLine - 1].Text;
        var indent = original[..original.IndexOf('#')];

        Assert.NotEmpty(indent);

        var mutation = Edit(document, map, new HostsEdit.RenameGroup(group.Name, "Renamed"));

        Assert.StartsWith(indent, Assert.Single(mutation.Changes).After, StringComparison.Ordinal);
        Assert.NotNull(Reparse(mutation).Find("Renamed"));
    }

    [Fact]
    public void Renaming_a_group_to_a_name_already_in_use_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<InvalidOperationException>(() =>
            HostsEditor.Apply(document, map, new HostsEdit.RenameGroup("DB Server", "Local Sites")));
    }

    [Fact]
    public void Renaming_a_group_to_its_own_name_changes_nothing()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.True(HostsEditor.Apply(document, map, new HostsEdit.RenameGroup("DB Server", "DB Server")).IsEmpty);
    }

    [Fact]
    public void Renaming_a_group_to_something_the_parser_would_read_differently_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<ArgumentException>(() =>
            HostsEditor.Apply(document, map, new HostsEdit.RenameGroup("DB Server", "Db:warn")));
    }

    // ── deleting ─────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_a_group_removes_it_and_leaves_the_others_untouched()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var others = map.Groups.Where(g => g.Name != "DB Server").Select(g => (g.Name, State: g.Describe())).ToArray();

        var mutation = Edit(document, map, new HostsEdit.DeleteGroup("DB Server"));
        var after = Reparse(mutation);

        Assert.Null(after.Find("DB Server"));
        Assert.Equal(others, after.Groups.Select(g => (g.Name, State: g.Describe())).ToArray());
    }

    /// <summary>
    /// Intranet's scope runs to the end of the file and has swallowed a Docker block. Those lines
    /// are not the group's — that is the analyzer's whole finding — so deleting the group must not
    /// take them with it.
    /// </summary>
    [Fact]
    public void Deleting_a_group_does_not_take_the_quarantined_lines_with_it()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var suspect = map.Find("Intranet")!.Options
            .SelectMany(option => option.SuspectLines)
            .Select(line => document.Lines[line - 1].Text)
            .ToArray();

        Assert.NotEmpty(suspect);

        var mutation = Edit(document, map, new HostsEdit.DeleteGroup("Intranet"));
        var text = mutation.Document.ToText();

        foreach (var line in suspect)
        {
            Assert.Contains(line, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deleting_an_option_leaves_its_siblings_and_the_group_in_place()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map, new HostsEdit.DeleteOption("DB Server", "Live"));
        var group = Reparse(mutation).Find("DB Server")!;

        Assert.Equal(["Test (db02)"], group.Options.Select(option => option.Name));
        Assert.Equal("Test (db02)", Assert.Single(group.ActiveOptions).Name);
    }

    [Fact]
    public void Deleting_an_option_removes_its_directive_and_every_line_it_owned()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var option = map.Find("DB Server", "Live")!;

        var mutation = Edit(document, map, new HostsEdit.DeleteOption("DB Server", "Live"));

        Assert.All(mutation.Changes, change => Assert.Equal(HostsLineChangeKind.Deleted, change.Kind));
        Assert.Equal(
            option.DirectiveLines.Concat(option.OwnedLines).Order(),
            mutation.Changes.Select(change => change.Line).Order());
    }

    // ── updating an option ───────────────────────────────────────────────────

    private static HostsEdit.UpdateOption Update(
        HostsMap map,
        HostsDocument document,
        string group,
        string option,
        string? newName = null,
        HostsSeverityLevel? severity = null,
        Func<List<HostsEntryEdit>, List<HostsEntryEdit>>? change = null)
    {
        var target = map.Find(group, option)!;

        var entries = target.OwnedLines
            .Select(line => new HostsEntryEdit(line, HostsLineValidator.DecomposeEntry(document.Lines[line - 1].Text)!))
            .ToList();

        return new HostsEdit.UpdateOption(
            group,
            option,
            newName ?? target.Name,
            severity ?? target.Severity,
            change is null ? entries : change(entries));
    }

    [Fact]
    public void Sending_an_option_back_unchanged_changes_nothing()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.True(HostsEditor.Apply(document, map, Update(map, document, "DB Server", "Live")).IsEmpty);
    }

    [Fact]
    public void Renaming_an_option_and_changing_its_flag_rewrites_only_its_directive()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map,
            Update(map, document, "DB Server", "Live", newName: "Production", severity: HostsSeverityLevel.Caution));

        var change = Assert.Single(mutation.Changes);
        Assert.Equal(HostsLineChangeKind.Edited, change.Kind);
        Assert.Equal("##value:Production:web", change.After);

        var option = Reparse(mutation).Find("DB Server", "Production")!;
        Assert.Equal(HostsSeverityLevel.Caution, option.Severity);
        Assert.Equal(2, option.TotalCount);
    }

    [Fact]
    public void Clearing_an_options_flag_removes_it_from_the_directive()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map,
            Update(map, document, "DB Server", "Live", severity: HostsSeverityLevel.Normal));

        Assert.Equal("##value:Live", Assert.Single(mutation.Changes).After);
        Assert.Equal(HostsSeverityLevel.Normal, Reparse(mutation).Find("DB Server", "Live")!.Severity);
    }

    [Fact]
    public void Editing_an_entry_rewrites_its_line_and_nothing_else()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map, Update(map, document, "DB Server", "Test (db02)", change: entries =>
        {
            entries[0] = entries[0] with { Value = entries[0].Value with { Address = "203.0.113.99" } };
            return entries;
        }));

        var change = Assert.Single(mutation.Changes);
        Assert.Equal(HostsLineChangeKind.Edited, change.Kind);

        var entry = Reparse(mutation).ActiveEntries.Single(e => e.Line == change.ResultLine);
        Assert.Equal("203.0.113.99", entry.Address);
    }

    /// <summary>
    /// Editing an entry is not a way to switch it on. A commented line that came back enabled would
    /// change what the machine resolves without the developer ever asking for it.
    /// </summary>
    [Fact]
    public void Editing_a_commented_entry_leaves_it_commented()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.False(map.Find("DB Server", "Live")!.IsOn);

        var mutation = Edit(document, map, Update(map, document, "DB Server", "Live", change: entries =>
        {
            entries[0] = entries[0] with { Value = entries[0].Value with { Hostnames = "renamed.example.com" } };
            return entries;
        }));

        Assert.False(HostsTokenizer.IsActive(Assert.Single(mutation.Changes).After));
        Assert.False(Reparse(mutation).Find("DB Server", "Live")!.IsOn);
    }

    [Fact]
    public void An_entry_left_out_of_the_list_is_deleted()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var before = map.Find("DB Server", "Test (db02)")!;

        var mutation = Edit(document, map, Update(map, document, "DB Server", "Test (db02)", change: entries =>
        {
            entries.RemoveAt(0);
            return entries;
        }));

        var change = Assert.Single(mutation.Changes);
        Assert.Equal(HostsLineChangeKind.Deleted, change.Kind);
        Assert.Equal(before.OwnedLines[0], change.Line);
        Assert.Equal(before.TotalCount - 1, Reparse(mutation).Find("DB Server", "Test (db02)")!.TotalCount);
    }

    [Fact]
    public void An_entry_with_no_line_number_is_added()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var before = map.Find("DB Server", "Test (db02)")!;

        var mutation = Edit(document, map, Update(map, document, "DB Server", "Test (db02)", change: entries =>
        {
            entries.Add(new HostsEntryEdit(0, new NewHostsEntry("203.0.113.80", "db03.example.com")));
            return entries;
        }));

        var change = Assert.Single(mutation.Changes);
        Assert.Equal(HostsLineChangeKind.Inserted, change.Kind);

        var after = Reparse(mutation).Find("DB Server", "Test (db02)")!;
        Assert.Equal(before.TotalCount + 1, after.TotalCount);

        // The option was on, so the new line is too — it must not come back reading "2 of 3".
        Assert.False(after.IsPartiallyOn);
    }

    [Fact]
    public void Adding_editing_and_deleting_in_one_go_produces_one_change_set()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = Edit(document, map, Update(map, document, "Intranet", "web01",
            newName: "primary",
            change: entries =>
            {
                entries[0] = entries[0] with { Value = entries[0].Value with { Address = "192.0.2.201" } };
                entries.RemoveAt(1);
                entries.Add(new HostsEntryEdit(0, new NewHostsEntry("192.0.2.202", "extra.intranet.example.com")));
                return entries;
            }));

        // The directive and one entry rewritten, one entry dropped, one added — one action, one
        // change set, one diff.
        Assert.Equal(2, mutation.Changes.Count(change => change.Kind == HostsLineChangeKind.Edited));
        Assert.Single(mutation.Changes, change => change.Kind == HostsLineChangeKind.Deleted);
        Assert.Single(mutation.Changes, change => change.Kind == HostsLineChangeKind.Inserted);

        var option = Reparse(mutation).Find("Intranet", "primary")!;
        Assert.Equal(2, option.TotalCount);
    }

    [Fact]
    public void Updating_an_option_never_touches_its_quarantined_lines()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var before = map.Find("Intranet", "web02")!;

        Assert.NotEmpty(before.SuspectLines);

        var mutation = Edit(document, map, Update(map, document, "Intranet", "web02", newName: "secondary"));

        Assert.DoesNotContain(mutation.Changes, change => before.SuspectLines.Contains(change.Line));
        Assert.Equal(before.SuspectLines.Count, Reparse(mutation).Find("Intranet", "secondary")!.SuspectLines.Count);
    }

    [Fact]
    public void An_entry_line_the_option_does_not_own_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var stranger = map.Find("Intranet", "web02")!.SuspectLines[0];

        Assert.Throws<InvalidOperationException>(() => HostsEditor.Apply(document, map,
            new HostsEdit.UpdateOption("DB Server", "Live", "Live", HostsSeverityLevel.Danger,
                [new HostsEntryEdit(stranger, new NewHostsEntry("127.0.0.1", "a.example.com"))])));
    }

    [Fact]
    public void Renaming_an_option_onto_a_sibling_is_refused()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<InvalidOperationException>(() =>
            HostsEditor.Apply(document, map, Update(map, document, "DB Server", "Live", newName: "Test (db02)")));
    }

    [Fact]
    public void An_option_tagged_only_line_by_line_cannot_be_edited_as_a_block()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        var inlineOnly = map.Groups
            .SelectMany(group => group.Options.Select(option => (group, option)))
            .First(pair => pair.option.DirectiveLines.Count == 0);

        Assert.Throws<InvalidOperationException>(() => HostsEditor.Apply(document, map,
            new HostsEdit.UpdateOption(inlineOnly.group.Name, inlineOnly.option.Name, "Renamed", HostsSeverityLevel.Normal, [])));
    }

    // ── the file itself ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(HostsSamples.CrlfBom)]
    [InlineData(HostsSamples.LfNoBom)]
    [InlineData(HostsSamples.MixedEndings)]
    public void Editing_keeps_the_files_encoding_and_line_endings(string sample)
    {
        var (document, map) = HostsSamples.Parse(sample);
        var group = map.Groups[0];

        var mutation = Edit(document, map, new HostsEdit.RenameGroup(group.Name, "Renamed"));

        var written = HostsDocumentCodec.Compose(mutation.Document);
        var reread = HostsDocumentCodec.FromBytes(document.Path, written, document.LastWriteTimeUtc);

        Assert.Equal(document.Preamble, reread.Preamble);
        Assert.Equal(document.Encoding.CodePage, reread.Encoding.CodePage);
        Assert.Equal(document.Lines.Count, reread.Lines.Count);

        foreach (var original in document.Lines)
        {
            Assert.Equal(original.NewLine, reread.Lines[original.Number - 1].NewLine);
        }
    }
}
