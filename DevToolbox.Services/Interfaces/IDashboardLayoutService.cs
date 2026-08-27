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

    /// <summary>Whether a group is kept off the dashboard.</summary>
    bool IsHidden(string? groupName);

    /// <summary>
    /// Hides a shown group, or shows a hidden one. Returns the new state. Nothing is deleted —
    /// see <see cref="DashboardLayout.Hidden"/>.
    /// </summary>
    Task<bool> ToggleHiddenAsync(string groupName);

    /// <summary>
    /// The hidden group names, so the toolbar can say how many there are. A hidden group with no
    /// affordance to bring it back is a deleted group with extra steps.
    /// </summary>
    IReadOnlyList<string> HiddenGroups { get; }

    /// <summary>Extra names this card answers to in the search box. Empty when it has none.</summary>
    IReadOnlyList<string> AliasesFor(AliasScope scope, string name);

    /// <summary>
    /// Replaces a card's aliases. An empty list removes the entry rather than writing a blank
    /// one, so the file only ever holds aliases that exist.
    /// </summary>
    Task SetAliasesAsync(AliasScope scope, string name, IEnumerable<string> aliases);

    // ---- card overrides ----------------------------------------------------------------------
    //
    // The layer that makes a scanned card editable. A scan is rebuilt from disk every time, so
    // these are stored as patches against the name the scan produced, and reapplied after it.

    /// <summary>
    /// The scan's group with the hand edits on top: renames, merges, relabelled locations, and the
    /// hidden cards dropped. Returns the group unchanged when it has no overrides, and otherwise a
    /// new group of new workspaces — never edits what it is given.
    /// </summary>
    WorkspaceGroup Customize(WorkspaceGroup group, bool includeHidden = false);

    /// <summary>What has been changed about one scanned card, or null. Keyed by the scanned name.</summary>
    CardOverride? CardOverrideFor(string? groupName, string? cardName);

    bool IsCardHidden(string? groupName, string? cardName);

    /// <summary>Whether this group has any hand edits at all, for the "reset" offer.</summary>
    bool HasCardOverrides(string? groupName);

    /// <summary>
    /// Renames a scanned card. A blank name, or the scanned name itself, drops the override so the
    /// card follows the file again. Carries the card's pin and aliases across with it.
    /// </summary>
    Task RenameCardAsync(string groupName, string cardName, string? newName);

    /// <summary>Keeps a scanned card off its group, or puts it back. Deletes nothing.</summary>
    Task SetCardHiddenAsync(string groupName, string cardName, bool hidden);

    /// <summary>
    /// Replaces the chip labels for one card's locations, keyed by path — the only thing here not
    /// keyed by name, because two locations on a card can share a name and usually do when this is
    /// what you reached for. Blank labels are dropped rather than stored.
    /// </summary>
    Task SetLocationNamesAsync(string groupName, string cardName, IReadOnlyDictionary<string, string> names);

    /// <summary>
    /// Folds <paramref name="fromCard"/> into <paramref name="intoCard"/>: its locations move over
    /// and it stops appearing on its own. Anything it had already absorbed comes along.
    /// </summary>
    Task MergeCardsAsync(string groupName, string intoCard, string fromCard);

    /// <summary>
    /// Undoes a merge. <paramref name="absorbed"/> names one card to release; null releases all of
    /// them.
    /// </summary>
    Task UnmergeCardAsync(string groupName, string cardName, string? absorbed = null);

    /// <summary>Drops every card override in a group — the way back from any arrangement.</summary>
    Task ResetCardsAsync(string groupName);

    /// <summary>
    /// Moves everything in this file that is keyed by a group's name onto its new name: the order
    /// position, the pins, the aliases, the hidden flag and the card overrides. Without it a rename
    /// silently drops the group's whole arrangement.
    /// </summary>
    Task RenameGroupAsync(string oldName, string newName);
}
