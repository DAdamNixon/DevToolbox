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
