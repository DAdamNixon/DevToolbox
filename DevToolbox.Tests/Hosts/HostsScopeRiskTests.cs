using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Quarantining lines that drifted into an option's scope.
/// <para>
/// The file this guards against is real: its last group has no closing directive, so the last
/// option owns a Docker Desktop block and two of the developer's own live entries. Two things went
/// wrong before the quarantine — the option read as switched on, and switching the group to a
/// sibling commented those entries out.
/// </para>
/// </summary>
public class HostsScopeRiskTests
{
    [Fact]
    public void Foreign_content_below_an_unterminated_group_is_quarantined()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var web02 = map.Find("Intranet", "web02")!;

        Assert.Equal([82, 83], web02.OwnedLines);
        Assert.Equal([84, 85, 86, 87, 88, 89, 97, 99, 100], web02.SuspectLines);
        Assert.True(web02.HasSuspectContent);
    }

    /// <summary>
    /// The lie the quarantine exists to stop: two enabled lines in the orphan region made an option
    /// whose own entries are all commented out report as switched on, which lit the tray.
    /// </summary>
    [Fact]
    public void An_option_whose_only_enabled_lines_are_quarantined_reads_as_off()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var web02 = map.Find("Intranet", "web02")!;

        Assert.False(web02.IsOn);
        Assert.Equal(0, web02.ActiveCount);
        Assert.Equal(2, web02.TotalCount);
        Assert.Equal(2, web02.SuspectActiveCount);
        Assert.Equal("off", map.Find("Intranet")!.Describe());
    }

    /// <summary>
    /// The false positive a blank-line rule alone would produce. This option's body is broken up by
    /// single blank lines, which is completely normal, and none of it may be quarantined.
    /// </summary>
    [Fact]
    public void Single_blank_lines_inside_a_body_are_not_treated_as_a_gap()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var me = map.Find("Local Sites", "Me")!;

        Assert.Empty(me.SuspectLines);
        Assert.Equal(Enumerable.Range(4, 16).Concat(Enumerable.Range(21, 4)).Concat([26, 27]), me.OwnedLines);
    }

    [Fact]
    public void Only_the_last_option_of_an_unterminated_group_is_suspected()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        Assert.Empty(map.Find("Intranet", "web01")!.SuspectLines);
    }

    [Fact]
    public void No_option_in_a_properly_closed_file_is_suspected()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.TabsInlineClear);

        Assert.All(map.Groups.SelectMany(g => g.Options), option => Assert.Empty(option.SuspectLines));
        Assert.Empty(map.Anomalies);
    }

    [Fact]
    public void Quarantined_content_is_reported_as_blocking_with_a_repair_point()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var anomaly = Assert.Single(map.Anomalies, a => a.Kind == HostsAnomalyKind.ForeignContentInOption);

        Assert.True(anomaly.BlocksApply);
        Assert.Equal(HostsSeverityLevel.Danger, anomaly.Severity);
        Assert.Equal("Intranet", anomaly.Group);
        Assert.Equal("web02", anomaly.Option);

        // A closing directive inserted before line 84 puts the Docker block and everything below it
        // out of scope.
        Assert.Equal(84, anomaly.SuggestedClearLine);
        Assert.Same(anomaly, Assert.Single(map.BlockingAnomalies));
    }

    [Fact]
    public void The_report_says_how_many_quarantined_lines_are_enabled()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var anomaly = Assert.Single(map.Anomalies, a => a.Kind == HostsAnomalyKind.ForeignContentInOption);

        Assert.Contains("9 lines", anomaly.Message);
        Assert.Contains("2 of them are enabled", anomaly.Message);

        // Consecutive runs collapse, so nine line numbers read as three groups.
        Assert.Equal("lines 84-89, 97, 99-100", anomaly.DescribeLines());
    }

    [Fact]
    public void Entries_in_the_quarantined_region_are_marked_as_such()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);

        var orphan = Assert.Single(map.Entries, entry => entry.Line == 97);

        Assert.True(orphan.IsSuspect);
        Assert.True(orphan.IsActive);
        Assert.Equal("Intranet", orphan.Group);
    }
}
