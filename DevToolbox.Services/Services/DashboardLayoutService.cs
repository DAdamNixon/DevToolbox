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

    private static string? KeyOf(Dictionary<string, List<string>> bucket, string? key) =>
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
