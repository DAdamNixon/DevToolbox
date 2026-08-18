using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The grammar: which groups and options a file describes, which are switched on, and what looks
/// wrong about it.
/// </summary>
public class HostsAnnotationParserTests
{
    // ── the byte-order-mark defect ───────────────────────────────────────────

    /// <summary>
    /// The legacy tool located a directive with <c>indexOf('##')</c> and treated any index above
    /// zero as a per-line tag. A file beginning with a byte-order mark therefore reported index 1
    /// for its very first line, which was then discarded — silently losing the whole first group
    /// from the menu.
    /// </summary>
    [Fact]
    public void The_first_group_survives_a_byte_order_mark()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var first = Assert.Single(map.Groups, g => g.Name == "Local Sites");
        Assert.Equal(["Me", "Adam", "Marsh"], first.Options.Select(o => o.Name));
        Assert.Equal(1, first.DirectiveLine);
    }

    /// <summary>
    /// The same fix in its general form: whitespace before a directive does not stop it opening a
    /// scope.
    /// </summary>
    [Fact]
    public void Whitespace_before_a_directive_still_opens_a_scope()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.IndentedDirectives);

        var group = Assert.Single(map.Groups);
        Assert.Equal("Indented", group.Name);
        Assert.Equal(["On", "Off"], group.Options.Select(o => o.Name));
        Assert.True(group.Find("On")!.IsOn);
        Assert.False(group.Find("Off")!.IsOn);
    }

    // ── groups, options and counts ───────────────────────────────────────────

    [Fact]
    public void Every_group_in_the_file_is_found()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Equal(
            ["Local Sites", "DB Server", "Public Web", "Services", "Intranet"],
            map.Groups.Select(g => g.Name));
    }

    [Fact]
    public void An_options_body_includes_the_blank_lines_that_break_it_up()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        // Lines 4-19, 21-24, 26-27: three runs separated by single blank lines, all one body.
        var me = map.Find("Local Sites", "Me")!;

        Assert.Equal(22, me.TotalCount);
        Assert.Equal(22, me.ActiveCount);
        Assert.True(me.IsOn);
        Assert.False(me.IsPartiallyOn);
        Assert.Empty(me.SuspectLines);
    }

    [Fact]
    public void A_commented_out_option_reads_as_off()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var adam = map.Find("Local Sites", "Adam")!;

        Assert.Equal(6, adam.TotalCount);
        Assert.Equal(0, adam.ActiveCount);
        Assert.False(adam.IsOn);
    }

    [Fact]
    public void The_active_option_of_a_group_is_identified()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var db = map.Find("DB Server")!;

        Assert.Equal("Test (db02)", Assert.Single(db.ActiveOptions).Name);
        Assert.Equal("Test (db02)", db.Describe());
        Assert.Equal(2, db.Find("Test (db02)")!.ActiveCount);
        Assert.Equal(0, db.Find("Live")!.ActiveCount);
    }

    [Fact]
    public void A_group_with_nothing_switched_on_describes_itself_as_off()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Equal("off", map.Find("Services")!.Describe());
    }

    [Fact]
    public void A_partially_enabled_option_reports_how_many()
    {
        var document = HostsDocumentCodec.FromBytes(
            "partial",
            System.Text.Encoding.UTF8.GetBytes(
                "##key:G\r\n##value:Half\r\n127.0.0.1 a.example.com\r\n# 127.0.0.1 b.example.com\r\n##clear\r\n"),
            DateTime.UtcNow);

        var option = HostsAnnotationParser.Parse(document).Find("G", "Half")!;

        Assert.True(option.IsOn);
        Assert.True(option.IsPartiallyOn);
        Assert.Equal("1 of 2", option.PartialLabel);
    }

    // ── severity ─────────────────────────────────────────────────────────────

    [Fact]
    public void Severity_flags_map_through_the_dialect()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Equal(HostsSeverityLevel.Danger, map.Find("DB Server", "Live")!.Severity);
        Assert.Equal(HostsSeverityLevel.Caution, map.Find("Public Web", "web01")!.Severity);
        Assert.Equal(HostsSeverityLevel.Normal, map.Find("DB Server", "Test (db02)")!.Severity);
    }

    /// <summary>
    /// The whole point of quarantining foreign lines: the only options actually switched on in this
    /// file are unflagged, so the tray must read normal. Before the quarantine, an orphan region
    /// two lines long made a caution-flagged option look enabled.
    /// </summary>
    [Fact]
    public void Active_severity_ignores_options_that_only_look_enabled()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Equal(HostsSeverityLevel.Normal, map.ActiveSeverity);
    }

    [Fact]
    public void Switching_a_dangerous_option_on_raises_the_active_severity()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var switched = HostsMutator.SetOption(document, map, "DB Server", "Live");

        Assert.Equal(HostsSeverityLevel.Danger, HostsAnnotationParser.Parse(switched.Document).ActiveSeverity);
    }

    [Theory]
    // Only a third token can be a flag, so an option genuinely called "warn" keeps its name.
    [InlineData("##value:warn", "warn", HostsSeverityLevel.Normal)]
    // An unrecognised third token belongs to the name rather than being read as a flag.
    [InlineData("##value:A:B", "A:B", HostsSeverityLevel.Normal)]
    [InlineData("##value:A:B:warn", "A:B", HostsSeverityLevel.Danger)]
    [InlineData("##value:Live:WARN", "Live", HostsSeverityLevel.Danger)]
    public void A_flag_is_only_read_when_the_dialect_defines_it(string directive, string name, HostsSeverityLevel severity)
    {
        var document = HostsDocumentCodec.FromBytes(
            "flags",
            System.Text.Encoding.UTF8.GetBytes($"##key:G\r\n{directive}\r\n127.0.0.1 a.example.com\r\n"),
            DateTime.UtcNow);

        var option = Assert.Single(HostsAnnotationParser.Parse(document).Find("G")!.Options);

        Assert.Equal(name, option.Name);
        Assert.Equal(severity, option.Severity);
    }

    /// <summary>The legacy parser lower-cased verbs but its writer compared them case-sensitively,
    /// so a mixed-case directive parsed and then silently never applied.</summary>
    [Fact]
    public void Verbs_are_matched_case_insensitively()
    {
        var document = HostsDocumentCodec.FromBytes(
            "case",
            System.Text.Encoding.UTF8.GetBytes(
                "##KEY:G\r\n##Value:On\r\n127.0.0.1 a.example.com\r\n##CLEAR\r\n127.0.0.1 outside.example.com\r\n"),
            DateTime.UtcNow);

        var map = HostsAnnotationParser.Parse(document);
        var group = Assert.Single(map.Groups);

        Assert.Equal("G", group.Name);
        Assert.Equal(HostsScopeEnd.Clear, group.EndKind);
        Assert.Equal(1, Assert.Single(group.Options).TotalCount);
    }

    // ── scope ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HostsSamples.LfNoBom, "DB Server", HostsScopeEnd.Clear)]
    [InlineData(HostsSamples.LfNoBom, "Local Sites", HostsScopeEnd.NextGroup)]
    [InlineData(HostsSamples.CrlfBom, "Intranet", HostsScopeEnd.EndOfFile)]
    [InlineData(HostsSamples.CrlfBom, "Services", HostsScopeEnd.NextGroup)]
    public void How_a_groups_scope_ends_is_recorded(string sample, string group, HostsScopeEnd expected)
    {
        var (_, map) = HostsSamples.Parse(sample);

        Assert.Equal(expected, map.Find(group)!.EndKind);
    }

    [Fact]
    public void Content_after_a_closing_directive_belongs_to_nobody()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        var outside = map.Entries.Where(entry => entry.Group is null).ToArray();

        Assert.Contains(outside, entry => entry.Hostnames.Contains("workmanager.local"));
        Assert.All(outside, entry => Assert.Null(entry.Option));
    }

    [Fact]
    public void Content_before_any_group_belongs_to_nobody()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.OptionBeforeGroup);

        Assert.Equal("Real", Assert.Single(map.Groups).Name);
        Assert.Contains(
            map.Anomalies,
            a => a.Kind == HostsAnomalyKind.OptionBeforeGroup && a.Lines.SequenceEqual([1]));
    }

    // ── inline directives ────────────────────────────────────────────────────

    [Fact]
    public void An_inline_directive_tags_only_its_own_line()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        var group = map.Find("ShippingServiceDNS")!;

        Assert.Equal(["Local", "SR02", "WebDev01", "Jorge"], group.Options.Select(o => o.Name));
        Assert.All(group.Options, option =>
        {
            Assert.Single(option.InlineLines);
            Assert.Empty(option.OwnedLines);
            Assert.Equal(1, option.TotalCount);
        });

        Assert.Equal("Local", Assert.Single(group.ActiveOptions).Name);
        Assert.Equal(HostsSeverityLevel.Danger, group.Find("SR02")!.Severity);
    }

    [Fact]
    public void An_inline_group_directive_is_ignored()
    {
        var document = HostsDocumentCodec.FromBytes(
            "inline-key",
            System.Text.Encoding.UTF8.GetBytes("127.0.0.1 a.example.com   ##key:Nope\r\n"),
            DateTime.UtcNow);

        Assert.Empty(HostsAnnotationParser.Parse(document).Groups);
    }

    // ── parked lines ─────────────────────────────────────────────────────────

    [Fact]
    public void Parked_lines_are_neither_counted_nor_owned()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.Parked);

        var live = map.Find("DB Server", "Live")!;

        Assert.Equal(2, live.ParkedLines.Count);
        Assert.Equal(1, live.TotalCount);
        Assert.Equal(0, live.ActiveCount);
    }

    // ── the alternate dialect ────────────────────────────────────────────────

    /// <summary>
    /// Without this, the defaults quietly become the only working values and the dialect is
    /// configuration in name only.
    /// </summary>
    [Fact]
    public void A_different_dialect_parses_its_own_file()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.AltDialect, HostsSamples.AlternateDialect);

        var group = Assert.Single(map.Groups);

        Assert.Equal("Database", group.Name);
        Assert.Equal(["Test", "Production"], group.Options.Select(o => o.Name));
        Assert.Equal(HostsSeverityLevel.Danger, group.Find("Production")!.Severity);
        Assert.Equal("Test", Assert.Single(group.ActiveOptions).Name);
        Assert.Equal(HostsScopeEnd.Clear, group.EndKind);
    }

    [Fact]
    public void The_default_dialect_finds_nothing_in_another_dialects_file()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.AltDialect);

        Assert.Empty(map.Groups);
        Assert.False(map.HasAnnotations);
    }

    [Fact]
    public void An_unusable_dialect_is_rejected_rather_than_failing_obscurely()
    {
        var document = HostsSamples.Load(HostsSamples.LfNoBom);

        Assert.Throws<InvalidOperationException>(
            () => HostsAnnotationParser.Parse(document, new HostsDialect { Prefix = string.Empty }));

        Assert.Throws<InvalidOperationException>(
            () => HostsAnnotationParser.Parse(document, new HostsDialect { GroupVerb = "same", OptionVerb = "same" }));
    }

    // ── entries ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_entry_records_its_address_and_hostnames()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var entry = Assert.Single(map.Entries, e => e.Line == 43);

        Assert.Equal("203.0.113.80", entry.Address);
        Assert.Equal(["db01.example.com"], entry.Hostnames);
        Assert.True(entry.IsActive);
        Assert.Equal("DB Server", entry.Group);
        Assert.Equal("Test (db02)", entry.Option);
    }

    [Fact]
    public void Text_in_brackets_after_the_hostnames_is_not_treated_as_a_hostname()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var entry = Assert.Single(map.Entries, e => e.Line == 51);

        Assert.Equal(["www.example.com"], entry.Hostnames);
        Assert.Equal("(all traffic)", entry.TrailingText);
    }

    [Fact]
    public void Entries_outside_any_group_are_still_collected()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        // Line 97 sits below the last group in the un-annotated remainder of the file. It has to be
        // collected, or the duplicate hostname below it can never be spotted.
        Assert.Contains(map.Entries, entry => entry.Line == 97 && entry.IsActive);
    }

    // ── anomalies ────────────────────────────────────────────────────────────

    [Fact]
    public void A_hostname_enabled_twice_is_reported()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var duplicate = Assert.Single(map.Anomalies, a => a.Kind == HostsAnomalyKind.DuplicateActiveHost);

        Assert.Equal([27, 100], duplicate.Lines);
        Assert.Contains("metrics.example.com", duplicate.Message);
    }

    [Fact]
    public void Bracketed_trailing_text_is_reported_once_for_the_whole_file()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var anomaly = Assert.Single(map.Anomalies, a => a.Kind == HostsAnomalyKind.TrailingTextAfterHostnames);

        Assert.Equal([51, 52, 57, 58, 68, 79, 80, 82, 83], anomaly.Lines);
    }

    [Fact]
    public void An_unterminated_last_group_is_reported()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var anomaly = Assert.Single(map.Anomalies, a => a.Kind == HostsAnomalyKind.UnterminatedTrailingScope);

        Assert.Equal("Intranet", anomaly.Group);
        Assert.Equal("web02", anomaly.Option);
    }

    /// <summary>
    /// An unterminated scope on its own costs nothing — plenty of safe files simply never got their
    /// closing directive — so it must not block a switch, or the warning that matters gets clicked
    /// through out of habit.
    /// </summary>
    [Fact]
    public void An_unterminated_scope_alone_does_not_block_a_switch()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.NoTrailingNewLine);

        Assert.Contains(map.Anomalies, a => a.Kind == HostsAnomalyKind.UnterminatedTrailingScope);
        Assert.Empty(map.BlockingAnomalies);
    }

    [Fact]
    public void A_file_that_is_closed_properly_reports_nothing_serious()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        Assert.Empty(map.BlockingAnomalies);
        Assert.DoesNotContain(map.Anomalies, a => a.Kind == HostsAnomalyKind.UnterminatedTrailingScope);
    }

    [Fact]
    public void Mixed_terminators_are_reported_but_not_serious()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.MixedEndings);

        Assert.Contains(map.Anomalies, a => a.Kind == HostsAnomalyKind.MixedNewLines);
        Assert.Empty(map.BlockingAnomalies);
    }

    [Fact]
    public void A_latin1_file_is_reported_as_such()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.Latin1);

        Assert.Contains(map.Anomalies, a => a.Kind == HostsAnomalyKind.NonUtf8Encoding);
    }

    /// <summary>
    /// Prose that drifted into an option's scope is reported by the risk analyzer in far more useful
    /// terms, so it must not also be reported line by line as a malformed entry.
    /// </summary>
    [Fact]
    public void Quarantined_prose_is_not_also_reported_as_a_malformed_entry()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.DoesNotContain(map.Anomalies, a => a.Kind == HostsAnomalyKind.MalformedEntry);
    }

    [Fact]
    public void An_empty_file_parses_to_nothing_without_complaint()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.Empty);

        Assert.Empty(map.Groups);
        Assert.Empty(map.Entries);
        Assert.Empty(map.Anomalies);
    }
}
