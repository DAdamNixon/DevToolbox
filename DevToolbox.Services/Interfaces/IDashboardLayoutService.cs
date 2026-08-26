using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Reads and writes Config/dashboardLayout.yaml: the order the group cards sit in, which
/// workspaces are pinned inside them, and the search aliases each card answers to.
/// <para>
/// Everything here is keyed by display name rather than by id, for the same reason the icon
/// overrides are: half the cards on the dashboard are scanned from disk and get a fresh
/// negative id on every rescan, so an id-keyed arrangement would survive exactly one scan.
/// </para>
/// </summary>
public interface IDashboardLayoutService
{
    /// <summary>The stored arrangement, read once and cached.</summary>
    Task<DashboardLayout> GetAsync();

    Task SaveAsync(DashboardLayout layout);

    /// <summary>
    /// Groups in the user's order: those named in <see cref="DashboardLayout.GroupOrder"/>
    /// first, in that order, then everything else in the order it arrived. Stable, so two
    /// groups that share a name keep their relative positions.
    /// </summary>
    IReadOnlyList<WorkspaceGroup> OrderGroups(IEnumerable<WorkspaceGroup> groups);

    /// <summary>
    /// Moves <paramref name="groupName"/> to where <paramref name="beforeGroupName"/>
    /// currently sits, and persists the whole visible order so the result does not depend on
    /// which groups happened to be listed before.
    /// <para>
    /// <paramref name="visibleOrder"/> is the order actually on screen. Passing it is what
    /// lets a first-ever drag write a complete list instead of a two-name one that leaves
    /// every other group floating at the end.
    /// </para>
    /// </summary>
    Task MoveGroupAsync(string groupName, string beforeGroupName, IEnumerable<string> visibleOrder);

    /// <summary>Whether a workspace is pinned to the front of its group.</summary>
    bool IsPinned(string groupName, string workspaceName);

    /// <summary>Pins an unpinned workspace, or unpins a pinned one. Returns the new state.</summary>
    Task<bool> TogglePinAsync(string groupName, string workspaceName);

    /// <summary>
    /// Workspaces with the pinned ones first, each block keeping the order it came in.
    /// </summary>
    IReadOnlyList<Workspace> OrderWorkspaces(string groupName, IEnumerable<Workspace> workspaces);

    /// <summary>Extra names this card answers to in the search box. Empty when it has none.</summary>
    IReadOnlyList<string> AliasesFor(AliasScope scope, string name);

    /// <summary>
    /// Replaces a card's aliases. An empty list removes the entry rather than writing a blank
    /// one, so the file only ever holds aliases that exist.
    /// </summary>
    Task SetAliasesAsync(AliasScope scope, string name, IEnumerable<string> aliases);
}
