using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models;

/// <summary>
/// Root of Config/dashboardLayout.yaml — how the Projects tab is arranged, as opposed to
/// what is on it.
/// <para>
/// Deliberately a separate file from workspaceGroups.yaml. Half of what the dashboard
/// shows is scanned from disk and rebuilt on every rescan, so there is nowhere in that
/// file to record that a scanned group sits second or that a scanned card is pinned.
/// Keying on names instead of ids means both halves are covered by one file, and a
/// rescan cannot lose any of it.
/// </para>
/// </summary>
public class DashboardLayout
{
    /// <summary>
    /// Group names in the order they appear. A group missing from this list sorts after
    /// every listed one, in its natural order, so a newly added or newly scanned group
    /// turns up at the bottom rather than silently first.
    /// </summary>
    public List<string> GroupOrder { get; set; } = new();

    /// <summary>
    /// Group name → the workspace names pinned inside it. Pinned cards sort to the front
    /// of their group and keep their relative order.
    /// </summary>
    public Dictionary<string, List<string>> Pinned { get; set; } = new();

    /// <summary>
    /// Group names kept off the dashboard. Hiding is not deleting: the group and everything in
    /// it stays in workspaceGroups.yaml, and the toolbar says how many are hidden so a group put
    /// away is never a group lost. Keyed by name like the rest of this file, so it works on a
    /// scanned group too.
    /// </summary>
    public List<string> Hidden { get; set; } = new();

    /// <summary>Extra names a card answers to in the search box.</summary>
    public AliasBook Aliases { get; set; } = new();

    /// <summary>
    /// Group name → the scanned cards inside it that have been edited by hand, keyed by the name
    /// the scan gave them.
    /// <para>
    /// This is the layer that makes a scanned card editable at all. Everything a scan produces is
    /// rebuilt from disk on every rescan, so there is nowhere in the scan's own output to record
    /// that two of its cards are really one project, or that a card should be called something
    /// else — the next rescan would throw it away. Recording it as a patch against the scanned
    /// name means the rescan still picks up new projects, and the edits reapply on top.
    /// </para>
    /// <para>
    /// Keyed by name rather than by path for the same reason the rest of this file is: a name is
    /// stable and hand-writable, where a path changes the moment a branch is added. Locations are
    /// the exception, and they say why.
    /// </para>
    /// </summary>
    public Dictionary<string, Dictionary<string, CardOverride>> Cards { get; set; } = new();
}

/// <summary>
/// What has been changed by hand about one scanned card. Absent fields mean "as scanned", so an
/// override only ever holds the difference.
/// </summary>
/// <remarks>
/// Every member here omits its default when written, so an override reads as the list of things
/// that were actually changed. Without it a card that had only been merged still wrote
/// <c>name:</c>, <c>hidden: false</c> and <c>locations: {}</c> beside it, which in a file meant to
/// be read and hand-edited is three lines of noise per card saying nothing.
/// <para>
/// Safe here in a way it would not be on <see cref="WorkspaceSource"/>: every default on this type
/// is also the C# default, so an omitted key and a written default load identically. A property
/// like <c>Enabled = true</c> could not do this — omitting <c>false</c> would read back as true.
/// </para>
/// </remarks>
public class CardOverride
{
    /// <summary>Shown instead of the scanned name. Null or blank keeps the scanned one.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? Name { get; set; }

    /// <summary>Kept off the group. Not deleted — the file it came from is untouched.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool Hidden { get; set; }

    /// <summary>
    /// Scanned card names whose locations are folded into this card, and which stop appearing on
    /// their own. The case config cannot express: a project whose two working copies produce two
    /// differently-named entries, because only one branch has the solution filter that renames it.
    /// <para>
    /// Stored as names, so a rescan that finds a third copy of an absorbed card folds that in too
    /// rather than leaving it stranded on a card of its own.
    /// </para>
    /// </summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Absorb { get; set; } = new();

    /// <summary>
    /// Location path → the label shown on its chip.
    /// <para>
    /// The one thing here keyed by path instead of by name, because a location name is not unique
    /// within a card and the whole reason to rename one is usually that it is not: two copies of
    /// <c>Products.sln</c> in one branch give a card two locations both called <c>dev</c>, and
    /// there is nothing but the path to tell the renamer which one it means. A path that no longer
    /// exists simply stops applying.
    /// </para>
    /// </summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Locations { get; set; } = new();

    /// <summary>Whether this override still says anything, i.e. whether it is worth storing.</summary>
    [YamlIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name)
                           && !Hidden
                           && Absorb.Count == 0
                           && Locations.Count == 0;
}

/// <summary>
/// Search aliases, split the way <see cref="IconOverrides"/> is: by what the name refers
/// to. Keyed by display name — the same identity the icon overrides use — so an alias
/// survives a rescan and can be hand-written without looking up an id.
/// </summary>
public class AliasBook
{
    public Dictionary<string, List<string>> Groups { get; set; } = new();

    public Dictionary<string, List<string>> Workspaces { get; set; } = new();
}

/// <summary>What a name refers to, for the alias lookups.</summary>
public enum AliasScope
{
    Group,
    Workspace
}
