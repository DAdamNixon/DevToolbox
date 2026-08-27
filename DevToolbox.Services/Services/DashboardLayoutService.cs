using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Config/dashboardLayout.yaml, and the ordering rules that read it. Follows
/// <see cref="IconService"/>: one cached snapshot, every write going straight to disk, and a
/// parse failure degrading to "no arrangement yet" rather than taking the dashboard down.
/// </summary>
public class DashboardLayoutService : IDashboardLayoutService
{
    private const string ConfigKey = "dashboardLayout";

    private readonly IYamlStorageService _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DashboardLayout? _layout;

    public DashboardLayoutService(IYamlStorageService storage)
    {
        _storage = storage;
    }

    public async Task<DashboardLayout> GetAsync()
    {
        if (_layout is not null)
        {
            return _layout;
        }

        await _gate.WaitAsync();
        try
        {
            if (_layout is null)
            {
                try
                {
                    _layout = await _storage.LoadAsync<DashboardLayout>(ConfigKey) ?? new DashboardLayout();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DashboardLayoutService: could not read {ConfigKey}.yaml — {ex.Message}");
                    _layout = new DashboardLayout();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _layout;
    }

    public async Task SaveAsync(DashboardLayout layout)
    {
        await _storage.SaveAsync(ConfigKey, layout);
        _layout = layout;
    }

    public IReadOnlyList<WorkspaceGroup> OrderGroups(IEnumerable<WorkspaceGroup> groups)
    {
        var order = _layout?.GroupOrder ?? new List<string>();
        var list = groups.ToList();

        if (order.Count == 0)
        {
            return list;
        }

        // int.MaxValue for anything unlisted, so a group added since the last drag lands at the
        // end. OrderBy is a stable sort, which is the whole reason it is used here: two groups
        // sharing a rank — an unlisted pair, or a saved and a scanned group with one name —
        // keep the order they arrived in instead of swapping about between renders.
        return list
            .OrderBy(g => RankOf(order, g.Name))
            .ToList();
    }

    public async Task MoveGroupAsync(string groupName, string beforeGroupName, IEnumerable<string> visibleOrder)
    {
        if (string.IsNullOrWhiteSpace(groupName) || groupName.Equals(beforeGroupName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var layout = await GetAsync();

        // Start from what is on screen, not from the stored list. The stored list may be empty
        // (nothing dragged yet) or stale (a group scanned since), and either way inserting into
        // it would move the card somewhere the user cannot see.
        var order = Deduplicate(visibleOrder);

        var from = IndexOf(order, groupName);
        if (from < 0)
        {
            return;
        }

        order.RemoveAt(from);

        var to = IndexOf(order, beforeGroupName);
        if (to < 0)
        {
            // Dropped past the last card: append.
            order.Add(groupName);
        }
        else
        {
            order.Insert(to, groupName);
        }

        layout.GroupOrder = order;
        await SaveAsync(layout);
    }

    public bool IsPinned(string groupName, string workspaceName)
    {
        var layout = _layout;
        if (layout is null || string.IsNullOrEmpty(workspaceName))
        {
            return false;
        }

        return TryGetBucket(layout.Pinned, groupName, out var pinned)
               && pinned.Any(name => name.Equals(workspaceName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> TogglePinAsync(string groupName, string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(workspaceName))
        {
            return false;
        }

        var layout = await GetAsync();

        var key = KeyOf(layout.Pinned, groupName) ?? groupName;
        if (!layout.Pinned.TryGetValue(key, out var pinned) || pinned is null)
        {
            pinned = new List<string>();
            layout.Pinned[key] = pinned;
        }

        var existing = pinned.FindIndex(name => name.Equals(workspaceName, StringComparison.OrdinalIgnoreCase));
        bool nowPinned;

        if (existing >= 0)
        {
            pinned.RemoveAt(existing);
            nowPinned = false;
        }
        else
        {
            pinned.Add(workspaceName);
            nowPinned = true;
        }

        // A group with nothing pinned should not leave an empty list behind in the file.
        if (pinned.Count == 0)
        {
            layout.Pinned.Remove(key);
        }

        await SaveAsync(layout);
        return nowPinned;
    }

    public IReadOnlyList<Workspace> OrderWorkspaces(string groupName, IEnumerable<Workspace> workspaces)
    {
        var list = workspaces.ToList();

        if (_layout is null || !TryGetBucket(_layout.Pinned, groupName, out var pinned) || pinned.Count == 0)
        {
            return list;
        }

        // Stable again: sorting on a bool moves the pinned cards to the front and leaves
        // everything else exactly where it was, which is what makes a pin read as a promotion
        // rather than a reshuffle of the whole group.
        return list
            .OrderBy(w => pinned.Any(name => name.Equals(w.Name, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ToList();
    }

    // ---- hiding ----------------------------------------------------------------------------

    public bool IsHidden(string? groupName)
    {
        var layout = _layout;
        if (layout is null || string.IsNullOrEmpty(groupName))
        {
            return false;
        }

        return IndexOf(layout.Hidden, groupName) >= 0;
    }

    public IReadOnlyList<string> HiddenGroups => _layout?.Hidden ?? (IReadOnlyList<string>)Array.Empty<string>();

    public async Task<bool> ToggleHiddenAsync(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        var layout = await GetAsync();

        var existing = IndexOf(layout.Hidden, groupName);
        bool nowHidden;

        if (existing >= 0)
        {
            layout.Hidden.RemoveAt(existing);
            nowHidden = false;
        }
        else
        {
            layout.Hidden.Add(groupName);
            nowHidden = true;
        }

        await SaveAsync(layout);
        return nowHidden;
    }

    // ---- card overrides ----------------------------------------------------------------------

    public CardOverride? CardOverrideFor(string? groupName, string? cardName)
    {
        var bucket = BucketOf(_layout, groupName);
        if (bucket is null || string.IsNullOrEmpty(cardName))
        {
            return null;
        }

        var key = KeyOf(bucket, cardName);
        return key is null ? null : bucket[key];
    }

    public bool IsCardHidden(string? groupName, string? cardName) =>
        CardOverrideFor(groupName, cardName)?.Hidden == true;

    public bool HasCardOverrides(string? groupName) => BucketOf(_layout, groupName)?.Count > 0;

    /// <summary>
    /// The scan's group with the hand edits applied on top: cards renamed, folded into each other,
    /// their locations relabelled, and the hidden ones dropped.
    /// <para>
    /// Returns new <see cref="Workspace"/> instances rather than editing the ones it is given. The
    /// scanned groups are held by <c>WorkspaceSourceService</c> and handed out to every caller, and
    /// this runs on every render pass that rebuilds the view models — renaming in place would apply
    /// the first override, then find nothing to rename on the second pass because the name it keys
    /// on had already gone.
    /// </para>
    /// </summary>
    public WorkspaceGroup Customize(WorkspaceGroup group, bool includeHidden = false)
    {
        var overrides = BucketOf(_layout, group.Name);
        if (overrides is null || overrides.Count == 0)
        {
            // The overwhelmingly common case: no edits on this group, so no copying and no
            // allocation. Handing back the same instance also keeps a saved group's Workspaces
            // list the one the dashboard writes back to disk.
            return group;
        }

        // Who is folded into whom. Built first because an absorber may be listed after the card it
        // absorbs, and one pass could not know.
        var absorbedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (absorber, patch) in overrides)
        {
            foreach (var absorbed in patch.Absorb)
            {
                // A card cannot absorb itself, and the first claim on a card wins. Both are about a
                // hand-edited file: the UI flattens merges as it makes them, so nothing it writes
                // needs either rule.
                if (!absorbed.Equals(absorber, StringComparison.OrdinalIgnoreCase))
                {
                    absorbedBy.TryAdd(absorbed, absorber);
                }
            }
        }

        var kept = new List<Workspace>();
        var byScannedName = new Dictionary<string, Workspace>(StringComparer.OrdinalIgnoreCase);
        var folded = new Dictionary<string, List<Workspace>>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in group.Workspaces)
        {
            var absorber = RootAbsorberOf(absorbedBy, workspace.Name);
            if (absorber is not null)
            {
                if (!folded.TryGetValue(absorber, out var list))
                {
                    folded[absorber] = list = new List<Workspace>();
                }

                list.Add(workspace);
                continue;
            }

            var patch = CardOverrideFor(group.Name, workspace.Name);

            if (patch?.Hidden == true && !includeHidden)
            {
                continue;
            }

            var copy = Copy(workspace, patch);
            byScannedName[workspace.Name] = copy;
            kept.Add(copy);
        }

        foreach (var (absorber, sources) in folded)
        {
            if (byScannedName.TryGetValue(absorber, out var target))
            {
                foreach (var source in sources)
                {
                    target.Locations.AddRange(source.Locations.Select(CopyLocation));
                }

                continue;
            }

            // The absorber is not in this scan — its file was renamed, moved or deleted since the
            // merge was made. The cards it was holding come back as themselves rather than
            // disappearing with it: a stale merge must not be able to eat a project.
            foreach (var source in sources)
            {
                var patch = CardOverrideFor(group.Name, source.Name);
                if (patch?.Hidden == true && !includeHidden)
                {
                    continue;
                }

                kept.Add(Copy(source, patch));
            }
        }

        // Relabelling and folding both disturb the order the scan sorted these into.
        foreach (var workspace in kept)
        {
            var patch = CardOverrideFor(group.Name, workspace.OverrideKey);
            if (patch is not null && patch.Locations.Count > 0)
            {
                foreach (var location in workspace.Locations)
                {
                    var key = KeyOf(patch.Locations, location.Path);
                    if (key is not null && !string.IsNullOrWhiteSpace(patch.Locations[key]))
                    {
                        location.Name = patch.Locations[key];
                    }
                }
            }

            workspace.Locations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        kept.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new WorkspaceGroup
        {
            Id = group.Id,
            Name = group.Name,
            Workspaces = kept,
            SourceName = group.SourceName,
            SourceNames = group.SourceNames,
            SourcePath = group.SourcePath,
            SourceIcon = group.SourceIcon
        };
    }

    /// <summary>
    /// The card <paramref name="name"/> ends up on, following the chain to whichever card is not
    /// itself absorbed — or null when it is not absorbed at all.
    /// <para>
    /// Chains only arise in a hand-edited file, since <see cref="MergeCardsAsync"/> flattens them
    /// as it writes them. Following one matters anyway: with <c>A</c> absorbing <c>B</c> and
    /// <c>B</c> absorbing <c>C</c>, stopping at <c>B</c> would look for a card that does not appear
    /// and strand <c>C</c> on its own.
    /// </para>
    /// <para>
    /// A cycle resolves to "not absorbed", so every card in it appears as itself. The file is
    /// describing something impossible, and showing all of them is both terminating and lossless —
    /// the mistake is visible and nothing has been swallowed by it.
    /// </para>
    /// </summary>
    private static string? RootAbsorberOf(Dictionary<string, string> absorbedBy, string name)
    {
        if (!absorbedBy.TryGetValue(name, out var current))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name, current };

        while (absorbedBy.TryGetValue(current, out var next))
        {
            if (!seen.Add(next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// A shallow copy carrying the override's name, and remembering what the scan called it so the
    /// override can still be found from the card.
    /// </summary>
    private static Workspace Copy(Workspace workspace, CardOverride? patch) => new()
    {
        Id = workspace.Id,
        Name = string.IsNullOrWhiteSpace(patch?.Name) ? workspace.Name : patch!.Name!,
        ScannedName = workspace.Name,
        GroupName = workspace.GroupName,
        SourceName = workspace.SourceName,

        // The locations are copied too: relabelling one below would otherwise write through to the
        // scan's own object and stick until the next rescan.
        Locations = workspace.Locations.Select(CopyLocation).ToList()
    };

    private static WorkspaceLocation CopyLocation(WorkspaceLocation location) => new()
    {
        Name = location.Name,
        Path = location.Path,
        Root = location.Root,
        Type = location.Type,
        Description = location.Description,

        // Recorded before any relabelling below, so the dialog can still show what clearing the
        // label would give back.
        ScannedName = location.ScannedLabel
    };

    /// <summary>
    /// Read-modify-write of one card's patch, cleaning up after itself: a patch that no longer
    /// says anything is removed, and a group with no patches left takes its entry with it.
    /// </summary>
    private async Task EditCardAsync(string groupName, string cardName, Action<CardOverride> edit)
    {
        var layout = await GetAsync();

        var groupKey = KeyOf(layout.Cards, groupName) ?? groupName;
        if (!layout.Cards.TryGetValue(groupKey, out var bucket) || bucket is null)
        {
            bucket = new Dictionary<string, CardOverride>();
            layout.Cards[groupKey] = bucket;
        }

        var cardKey = KeyOf(bucket, cardName) ?? cardName;
        if (!bucket.TryGetValue(cardKey, out var patch) || patch is null)
        {
            patch = new CardOverride();
            bucket[cardKey] = patch;
        }

        edit(patch);

        // An override that no longer says anything is removed, and a group with no overrides left
        // takes its entry with it. The file should read as the list of things that have been
        // changed, not as a graveyard of cards that were once touched.
        if (patch.IsEmpty)
        {
            bucket.Remove(cardKey);
        }

        if (bucket.Count == 0)
        {
            layout.Cards.Remove(groupKey);
        }

        await SaveAsync(layout);
    }

    public async Task RenameCardAsync(string groupName, string cardName, string? newName)
    {
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(cardName))
        {
            return;
        }

        var before = DisplayNameOf(groupName, cardName);
        var trimmed = newName?.Trim();

        // Renaming back to the scanned name drops the override rather than storing the same string
        // twice — and then a rescan that renames the file follows the file again.
        var stored = string.Equals(trimmed, cardName, StringComparison.Ordinal) ? null : trimmed;

        await EditCardAsync(groupName, cardName, patch => patch.Name = stored);

        var after = DisplayNameOf(groupName, cardName);
        if (!after.Equals(before, StringComparison.Ordinal))
        {
            // The pin and the aliases are keyed by what is on the card, so they move with it.
            // Without this, renaming a pinned card silently unpins it.
            await MoveCardKeysAsync(groupName, before, after);
        }
    }

    public Task SetCardHiddenAsync(string groupName, string cardName, bool hidden) =>
        EditCardAsync(groupName, cardName, patch => patch.Hidden = hidden);

    public Task SetLocationNamesAsync(string groupName, string cardName, IReadOnlyDictionary<string, string> names) =>
        EditCardAsync(groupName, cardName, patch =>
        {
            patch.Locations = names
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        });

    public async Task MergeCardsAsync(string groupName, string intoCard, string fromCard)
    {
        if (string.IsNullOrWhiteSpace(intoCard)
            || string.IsNullOrWhiteSpace(fromCard)
            || intoCard.Equals(fromCard, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Merging a card that had absorbed others brings its whole set along, and its own override
        // goes — otherwise the file would claim a card that no longer appears absorbs things.
        var moving = CardOverrideFor(groupName, fromCard);
        var alsoMoving = moving?.Absorb.ToList() ?? new List<string>();
        var movingLocations = moving?.Locations ?? new Dictionary<string, string>();

        await EditCardAsync(groupName, intoCard, patch =>
        {
            foreach (var name in alsoMoving.Append(fromCard))
            {
                if (!patch.Absorb.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.Equals(intoCard, StringComparison.OrdinalIgnoreCase))
                {
                    patch.Absorb.Add(name);
                }
            }

            // Labels the absorbed card's locations already had are kept: they are keyed by path,
            // so they mean the same thing on the card those paths have just moved to.
            foreach (var (path, label) in movingLocations)
            {
                patch.Locations.TryAdd(path, label);
            }
        });

        await EditCardAsync(groupName, fromCard, patch =>
        {
            patch.Name = null;
            patch.Hidden = false;
            patch.Absorb.Clear();
            patch.Locations.Clear();
        });
    }

    public Task UnmergeCardAsync(string groupName, string cardName, string? absorbed = null) =>
        EditCardAsync(groupName, cardName, patch =>
        {
            if (absorbed is null)
            {
                patch.Absorb.Clear();
                return;
            }

            var index = patch.Absorb.FindIndex(a => a.Equals(absorbed, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                patch.Absorb.RemoveAt(index);
            }
        });

    public async Task ResetCardsAsync(string groupName)
    {
        var layout = await GetAsync();

        var key = KeyOf(layout.Cards, groupName);
        if (key is null)
        {
            return;
        }

        layout.Cards.Remove(key);
        await SaveAsync(layout);
    }

    /// <summary>What a card is currently shown as.</summary>
    private string DisplayNameOf(string groupName, string cardName)
    {
        var patch = CardOverrideFor(groupName, cardName);
        return string.IsNullOrWhiteSpace(patch?.Name) ? cardName : patch!.Name!;
    }

    /// <summary>Carries a card's pin and aliases across a rename, so neither is quietly lost.</summary>
    private async Task MoveCardKeysAsync(string groupName, string from, string to)
    {
        var layout = await GetAsync();
        var changed = false;

        if (TryGetBucket(layout.Pinned, groupName, out var pinned))
        {
            var index = pinned.FindIndex(n => n.Equals(from, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                pinned[index] = to;
                changed = true;
            }
        }

        var aliasKey = KeyOf(layout.Aliases.Workspaces, from);
        if (aliasKey is not null)
        {
            var aliases = layout.Aliases.Workspaces[aliasKey];
            layout.Aliases.Workspaces.Remove(aliasKey);
            layout.Aliases.Workspaces[to] = aliases;
            changed = true;
        }

        if (changed)
        {
            await SaveAsync(layout);
        }
    }

    // ---- renaming a group --------------------------------------------------------------------

    public async Task RenameGroupAsync(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName)
            || string.IsNullOrWhiteSpace(newName)
            || oldName.Equals(newName, StringComparison.Ordinal))
        {
            return;
        }

        var layout = await GetAsync();

        // Every one of these is keyed by group display name, so a rename that does not move them
        // all silently drops the group's arrangement. This was already happening to hand-made
        // groups: renaming one lost its order position, its pins, its aliases and its hidden flag,
        // and nothing said so.
        var orderIndex = IndexOf(layout.GroupOrder, oldName);
        if (orderIndex >= 0)
        {
            layout.GroupOrder[orderIndex] = newName;
        }

        MoveKey(layout.Pinned, oldName, newName);
        MoveKey(layout.Aliases.Groups, oldName, newName);
        MoveKey(layout.Cards, oldName, newName);

        var hiddenIndex = IndexOf(layout.Hidden, oldName);
        if (hiddenIndex >= 0)
        {
            layout.Hidden[hiddenIndex] = newName;
        }

        await SaveAsync(layout);
    }

    private static void MoveKey<T>(Dictionary<string, T> bucket, string from, string to)
    {
        var key = KeyOf(bucket, from);
        if (key is null)
        {
            return;
        }

        var value = bucket[key];
        bucket.Remove(key);
        bucket[to] = value;
    }

    private static Dictionary<string, CardOverride>? BucketOf(DashboardLayout? layout, string? groupName)
    {
        if (layout is null || string.IsNullOrEmpty(groupName))
        {
            return null;
        }

        var key = KeyOf(layout.Cards, groupName);
        return key is null ? null : layout.Cards[key];
    }

    public IReadOnlyList<string> AliasesFor(AliasScope scope, string name)
    {
        var layout = _layout;
        if (layout is null || string.IsNullOrEmpty(name))
        {
            return Array.Empty<string>();
        }

        return TryGetBucket(BucketFor(layout, scope), name, out var aliases)
            ? aliases
            : Array.Empty<string>();
    }

    public async Task SetAliasesAsync(AliasScope scope, string name, IEnumerable<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var layout = await GetAsync();
        var bucket = BucketFor(layout, scope);

        // Re-cased names are the same card, so the old key goes whatever case it was stored in.
        var existingKey = KeyOf(bucket, name);
        if (existingKey is not null)
        {
            bucket.Remove(existingKey);
        }

        var cleaned = Deduplicate(aliases);
        if (cleaned.Count > 0)
        {
            bucket[name] = cleaned;
        }

        await SaveAsync(layout);
    }

    private static Dictionary<string, List<string>> BucketFor(DashboardLayout layout, AliasScope scope) =>
        scope == AliasScope.Group ? layout.Aliases.Groups : layout.Aliases.Workspaces;

    /// <summary>Position in the stored order, or <c>int.MaxValue</c> for a group not in it.</summary>
    private static int RankOf(List<string> order, string name)
    {
        var index = IndexOf(order, name);
        return index < 0 ? int.MaxValue : index;
    }

    private static int IndexOf(List<string> names, string? name) =>
        string.IsNullOrEmpty(name)
            ? -1
            : names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Case-insensitive lookup into a hand-editable dictionary. YamlDotNet gives back whatever
    /// case the file used, and the same name typed by hand rarely matches it exactly.
    /// </summary>
    private static bool TryGetBucket(Dictionary<string, List<string>> bucket, string? key, out List<string> value)
    {
        var match = KeyOf(bucket, key);
        if (match is not null)
        {
            value = bucket[match] ?? new List<string>();
            return true;
        }

        value = new List<string>();
        return false;
    }

    /// <summary>
    /// Generic over the value, because everything in this file is a name-keyed dictionary written
    /// by hand as often as by the UI: pins and aliases hold lists, the card overrides hold patches,
    /// and a location label is keyed by a path a person will re-case without thinking about it.
    /// </summary>
    private static string? KeyOf<T>(Dictionary<string, T> bucket, string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : bucket.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trimmed, blank-free, first-spelling-wins.</summary>
    private static List<string> Deduplicate(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
