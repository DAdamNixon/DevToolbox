using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Describing a group by where its entries point.
/// <para>
/// This is what lets the tab give every card a distinct glyph without any code ever looking at a
/// group's <em>name</em>. Reading "DB Server" and choosing a database picture would work perfectly
/// and would bake one team's vocabulary into a tool built on the premise that it knows none.
/// </para>
/// </summary>
public class HostsGroupShapeTests
{
    private static HostsGroupShape ShapeOf(string text, string group)
    {
        var (_, map) = HostsSamples.ParseText(text);
        return HostsGroupShapes.Of(map.Find(group)!, map);
    }

    [Fact]
    public void Everything_pointing_back_at_this_machine_is_loopback() =>
        Assert.Equal(HostsGroupShape.Loopback, ShapeOf(
            """
            ##key:Local
            ##value:Me
            127.0.0.1 a.example.com
            127.0.0.1 b.example.com
            ##clear

            """, "Local"));

    [Fact]
    public void The_v6_loopback_counts_too() =>
        Assert.Equal(HostsGroupShape.Loopback, ShapeOf(
            """
            ##key:Local
            ##value:Me
            ::1 a.example.com
            127.0.0.1 b.example.com
            ##clear

            """, "Local"));

    /// <summary>
    /// The case a real file made obvious: a "whose copy am I using" group offers your own machine
    /// alongside two colleagues'. Judging it by the spread of the other options describes it as a
    /// choice between networks and loses the one fact about it anybody cares about.
    /// </summary>
    [Fact]
    public void A_group_offering_this_machine_among_others_still_counts_as_local() =>
        Assert.Equal(HostsGroupShape.LocalOrRemote, ShapeOf(
            """
            ##key:Sites
            ##value:Me
            127.0.0.1 a.example.com
            ##value:Colleague
            # 198.51.100.58 a.example.com
            ##clear

            """, "Sites"));

    [Fact]
    public void Addresses_on_one_network_are_a_single_destination() =>
        Assert.Equal(HostsGroupShape.SingleNetwork, ShapeOf(
            """
            ##key:Db
            ##value:Test
            203.0.113.80 a.example.com
            ##value:Spare
            # 203.0.113.91 a.example.com
            ##clear

            """, "Db"));

    [Fact]
    public void Addresses_on_different_networks_are_a_choice_between_destinations() =>
        Assert.Equal(HostsGroupShape.SeveralNetworks, ShapeOf(
            """
            ##key:Db
            ##value:Test
            203.0.113.80 a.example.com
            ##value:Live
            # 198.51.100.11 a.example.com
            ##clear

            """, "Db"));

    [Fact]
    public void A_group_whose_lines_are_not_entries_says_nothing() =>
        Assert.Equal(HostsGroupShape.Unknown, ShapeOf(
            """
            ##key:Notes
            ##value:One
            # something that is not an entry
            ##clear

            """, "Notes"));

    /// <summary>
    /// Commented lines still count: an option that is switched off is still part of what the group
    /// is for, and a card should not change its picture when you switch it.
    /// </summary>
    [Fact]
    public void Switching_a_group_off_does_not_change_what_it_is()
    {
        const string Text = """
            ##key:Db
            ##value:Test
            203.0.113.80 a.example.com
            ##clear

            """;

        var (document, map) = HostsSamples.ParseText(Text);
        var before = HostsGroupShapes.Of(map.Find("Db")!, map);

        var off = HostsMutator.SetOption(document, map, "Db", null);
        var afterMap = HostsAnnotationParser.Parse(off.Document);

        Assert.Equal(before, HostsGroupShapes.Of(afterMap.Find("Db")!, afterMap));
    }

    /// <summary>
    /// Intranet's scope runs to the end of the file and swallows a Docker block pointing somewhere
    /// else entirely. Letting those lines vote would describe the group as spanning networks it has
    /// nothing to do with — the same lie the analyzer already stops them telling about its counts.
    /// </summary>
    [Fact]
    public void Quarantined_lines_do_not_get_a_vote()
    {
        var (_, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var group = map.Find("Intranet")!;

        Assert.NotEmpty(group.Options.SelectMany(option => option.SuspectLines));
        Assert.Equal(HostsGroupShape.SingleNetwork, HostsGroupShapes.Of(group, map));
    }
}
