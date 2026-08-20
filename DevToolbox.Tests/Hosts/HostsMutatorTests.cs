using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Switching a group, and the guarantee that it touches nothing else.
/// </summary>
public class HostsMutatorTests
{
    private static HostsMutation Switch(string sample, string group, string? option, bool includeSuspect = false)
    {
        var (document, map) = HostsSamples.Parse(sample);
        var mutation = HostsMutator.SetOption(document, map, group, option, includeSuspect);

        // Every mutation in these tests is also put through the checker, so a change that alters
        // content rather than markers fails here rather than being asserted around.
        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);

        return mutation;
    }

    // ── the ordinary case ────────────────────────────────────────────────────

    [Fact]
    public void Switching_a_group_enables_one_option_and_comments_its_siblings()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "DB Server", "Live");

        Assert.Equal(
            [
                new(43, HostsLineChangeKind.Commented),
                new(44, HostsLineChangeKind.Commented),
                new(46, HostsLineChangeKind.Uncommented),
                new(47, HostsLineChangeKind.Uncommented),
            ],
            mutation.Changes.Select(c => new LineAndKind(c.Line, c.Kind)));
    }

    [Fact]
    public void The_switch_takes_effect_when_reparsed()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "DB Server", "Live");

        var group = HostsAnnotationParser.Parse(mutation.Document).Find("DB Server")!;

        Assert.Equal("Live", Assert.Single(group.ActiveOptions).Name);
    }

    [Fact]
    public void Turning_a_group_off_comments_every_option()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "Local Sites", null);

        Assert.Equal(22, mutation.Changes.Count);
        Assert.All(mutation.Changes, change => Assert.Equal(HostsLineChangeKind.Commented, change.Kind));
        Assert.Equal("off", HostsAnnotationParser.Parse(mutation.Document).Find("Local Sites")!.Describe());
    }

    [Fact]
    public void Switching_to_the_option_that_is_already_on_changes_nothing()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "DB Server", "Test (db02)");

        Assert.True(mutation.IsEmpty);
    }

    [Fact]
    public void Only_the_named_groups_lines_are_touched()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var dbServer = map.Find("DB Server")!;

        var mutation = HostsMutator.SetOption(document, map, "DB Server", "Live");

        Assert.All(
            mutation.Changes,
            change => Assert.InRange(change.Line, dbServer.DirectiveLine, dbServer.EndLine));
    }

    [Fact]
    public void A_switch_stops_at_a_closing_directive()
    {
        var mutation = Switch(HostsSamples.TabsInlineClear, "ShippingServiceDNS", "SR02");

        // Line 40 is the closing directive; the personal entries below it must be untouched.
        Assert.All(mutation.Changes, change => Assert.True(change.Line < 40));
    }

    // ── the disaster this feature exists to prevent ──────────────────────────

    /// <summary>
    /// Before the quarantine, switching this group to its other option would have commented out
    /// <c>locations.example.com</c> and <c>metrics.example.com</c> — two entries the developer
    /// depends on — and uncommented a Docker Desktop block.
    /// </summary>
    [Fact]
    public void Switching_an_unterminated_group_leaves_the_quarantined_lines_alone()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "Intranet", "web01");

        Assert.All(mutation.Changes, change => Assert.True(change.Line < 84));

        // Only 79 and 80 — the sibling option's own lines, 82 and 83, are already commented, so
        // commenting them is a no-op and is not written. A change set never contains a line whose
        // bytes would not move.
        Assert.Equal(new[] { 79, 80 }, mutation.Changes.Select(c => c.Line));
    }

    [Fact]
    public void A_change_set_never_lists_a_line_whose_bytes_would_not_move()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "Intranet", "web01");

        Assert.All(mutation.Changes, change => Assert.NotEqual(change.Before, change.After));
    }

    [Fact]
    public void The_developers_own_entries_below_the_group_still_resolve_afterwards()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "Intranet", "web01");

        var entries = HostsAnnotationParser.Parse(mutation.Document).ActiveEntries.ToArray();

        Assert.Contains(entries, entry => entry.Hostnames.Contains("locations.example.com"));
        Assert.Contains(entries, entry => entry.Hostnames.Contains("metrics.example.com"));
    }

    /// <summary>
    /// Sweeping the quarantined lines in is possible, but only deliberately — this is what the
    /// confirmation dialog's diff is showing before a developer agrees to it.
    /// </summary>
    [Fact]
    public void Quarantined_lines_are_only_touched_when_explicitly_included()
    {
        var mutation = Switch(HostsSamples.CrlfBom, "Intranet", "web01", includeSuspect: true);

        Assert.Contains(mutation.Changes, c => c.Line == 97 && c.Kind == HostsLineChangeKind.Commented);
        Assert.Contains(mutation.Changes, c => c.Line == 100 && c.Kind == HostsLineChangeKind.Commented);
    }

    // ── parked lines ─────────────────────────────────────────────────────────

    [Fact]
    public void Parked_lines_are_never_enabled_by_a_switch()
    {
        var mutation = Switch(HostsSamples.Parked, "DB Server", "Live");

        Assert.Equal([3, 5], mutation.Changes.Select(c => c.Line));
        Assert.DoesNotContain(mutation.Changes, change => change.Line is 6 or 7);
    }

    [Fact]
    public void Parked_lines_are_never_commented_by_a_switch_either()
    {
        var mutation = Switch(HostsSamples.Parked, "DB Server", "Test");

        Assert.DoesNotContain(mutation.Changes, change => change.Line is 6 or 7);
    }

    // ── inline directives ────────────────────────────────────────────────────

    [Fact]
    public void An_inline_directive_survives_being_toggled()
    {
        var mutation = Switch(HostsSamples.TabsInlineClear, "ShippingServiceDNS", "SR02");

        var enabled = Assert.Single(mutation.Changes, c => c.Kind == HostsLineChangeKind.Uncommented);

        Assert.Contains("##value:SR02:warn", enabled.After);
        Assert.StartsWith("192.0.2.146", enabled.After);
    }

    [Fact]
    public void Switching_an_inline_group_moves_the_tag_between_lines()
    {
        var mutation = Switch(HostsSamples.TabsInlineClear, "ShippingServiceDNS", "SR02");

        var group = HostsAnnotationParser.Parse(mutation.Document).Find("ShippingServiceDNS")!;

        Assert.Equal("SR02", Assert.Single(group.ActiveOptions).Name);
    }

    // ── stability ────────────────────────────────────────────────────────────

    [Fact]
    public void Applying_the_same_switch_twice_is_a_no_op_the_second_time()
    {
        var first = Switch(HostsSamples.CrlfBom, "DB Server", "Live");

        var map = HostsAnnotationParser.Parse(first.Document);
        var second = HostsMutator.SetOption(first.Document, map, "DB Server", "Live");

        Assert.True(second.IsEmpty);
    }

    /// <summary>
    /// Switching back and forth must settle rather than slowly rewriting the file. The first return
    /// trip does normalise <c>#addr</c> to <c>&#35; addr</c>, which is the legacy writer's spacing;
    /// after that the bytes stop moving.
    /// </summary>
    [Fact]
    public void Switching_back_and_forth_settles_after_one_round_trip()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var afterOne = RoundTrip(document);
        var afterTwo = RoundTrip(afterOne);

        Assert.Equal(HostsDocumentCodec.Compose(afterOne), HostsDocumentCodec.Compose(afterTwo));

        static HostsDocument RoundTrip(HostsDocument start)
        {
            var toLive = HostsMutator.SetOption(start, HostsAnnotationParser.Parse(start), "DB Server", "Live");
            var back = HostsMutator.SetOption(
                toLive.Document,
                HostsAnnotationParser.Parse(toLive.Document),
                "DB Server",
                "Test (db02)");

            return back.Document;
        }
    }

    [Fact]
    public void A_switch_never_changes_the_line_count_or_the_byte_order_mark()
    {
        var (document, _) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var mutation = Switch(HostsSamples.CrlfBom, "DB Server", "Live");

        Assert.Equal(document.Lines.Count, mutation.Document.Lines.Count);
        Assert.True(mutation.Document.HasByteOrderMark);
        Assert.All(mutation.Document.Lines, line => Assert.Equal("\r\n", line.NewLine));
    }

    // ── refusing to guess ────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_group_is_an_error_rather_than_a_silent_no_op()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<KeyNotFoundException>(() => HostsMutator.SetOption(document, map, "Nope", "web01"));
    }

    [Fact]
    public void An_unknown_option_is_an_error_too()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<KeyNotFoundException>(() => HostsMutator.SetOption(document, map, "DB Server", "Nope"));
    }

    // ── the repair ───────────────────────────────────────────────────────────

    [Fact]
    public void Inserting_a_closing_directive_takes_the_orphan_region_out_of_scope()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var anomaly = map.Anomalies.Single(a => a.Kind == HostsAnomalyKind.ForeignContentInOption);

        var mutation = HostsMutator.InsertClear(document, map.Dialect, anomaly.SuggestedClearLine!.Value);
        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);

        Assert.Equal(document.Lines.Count + 1, mutation.Document.Lines.Count);
        Assert.Equal("##clear", mutation.Document.Lines[83].Text);

        var repaired = HostsAnnotationParser.Parse(mutation.Document);
        var web02 = repaired.Find("Intranet", "web02")!;

        Assert.Empty(web02.SuspectLines);
        Assert.Equal([82, 83], web02.OwnedLines);
        Assert.Empty(repaired.BlockingAnomalies);
        Assert.DoesNotContain(repaired.Anomalies, a => a.Kind == HostsAnomalyKind.UnterminatedTrailingScope);
    }

    [Fact]
    public void After_the_repair_the_orphan_entries_belong_to_nobody()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var mutation = HostsMutator.InsertClear(document, map.Dialect, 84);
        var repaired = HostsAnnotationParser.Parse(mutation.Document);

        // Line 97 has shifted to 98 by the insertion.
        var orphan = Assert.Single(repaired.Entries, entry => entry.Line == 98);

        Assert.Null(orphan.Group);
        Assert.False(orphan.IsSuspect);
        Assert.True(orphan.IsActive);
    }

    [Fact]
    public void The_inserted_directive_uses_the_files_own_newline_style()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.LfNoBom);

        var mutation = HostsMutator.InsertClear(document, map.Dialect, 6);

        Assert.Equal("\n", mutation.Document.Lines[5].NewLine);
        Assert.All(mutation.Document.Lines, line => Assert.NotEqual("\r\n", line.NewLine));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    // Past the last line: a closing directive with nothing after it excludes nothing.
    [InlineData(101)]
    public void A_closing_directive_cannot_be_inserted_outside_the_file(int line)
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Throws<ArgumentOutOfRangeException>(() => HostsMutator.InsertClear(document, map.Dialect, line));
    }

    private readonly record struct LineAndKind(int Line, HostsLineChangeKind Kind);
}
