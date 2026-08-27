using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// Config/dashboardLayout.yaml: the group order, the pins and the search aliases.
/// <para>
/// Everything in it is keyed by display name rather than by id, because half the cards on the
/// dashboard are scanned from disk and handed a fresh negative id on every rescan. These tests
/// are mostly about the consequences of that — a name that arrives in a different case, a name
/// that is not in the stored order at all, a rescan that renumbers everything.
/// </para>
/// </summary>
public class DashboardLayoutTests
{
    /// <summary>
    /// Same shape as the fake in <see cref="LogConfigTests"/>: real YAML round-tripping, no disk.
    /// Round-tripping rather than holding the object matters here — the case-insensitive lookups
    /// exist because YamlDotNet hands back whatever case the file used.
    /// </summary>
    private sealed class FakeYamlStorage : IYamlStorageService
    {
        private static readonly ISerializer Serializer =
            new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

        private static readonly IDeserializer Deserializer =
            new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public string StorageDirectory => "in-memory";

        public int Writes { get; private set; }

        public void Seed(string fileName, string yaml) => _files[fileName] = yaml;

        public Task SaveAsync<T>(string fileName, T data)
        {
            Writes++;
            _files[fileName] = Serializer.Serialize(data);
            return Task.CompletedTask;
        }

        public Task<T?> LoadAsync<T>(string fileName) =>
            Task.FromResult(_files.TryGetValue(fileName, out var yaml) ? Deserializer.Deserialize<T>(yaml) : default);

        public Task<bool> DeleteAsync(string fileName) => Task.FromResult(_files.Remove(fileName));

        public Task<List<string>> ListFilesAsync() => Task.FromResult(_files.Keys.ToList());
    }

    private static WorkspaceGroup Group(string name, params string[] workspaces) => new()
    {
        Id = 1,
        Name = name,
        Workspaces = workspaces.Select(w => new Workspace { Id = 1, Name = w, GroupName = name }).ToList()
    };

    private static async Task<(DashboardLayoutService Service, FakeYamlStorage Storage)> LoadedAsync(string? yaml = null)
    {
        var storage = new FakeYamlStorage();
        if (yaml is not null)
        {
            storage.Seed("dashboardLayout", yaml);
        }

        var service = new DashboardLayoutService(storage);

        // Every synchronous read answers from the cached snapshot, exactly as the dashboard does
        // it: Index awaits GetAsync once during initialisation and the cards read it during render.
        await service.GetAsync();

        return (service, storage);
    }

    // ---- group order ------------------------------------------------------------------------

    [Fact]
    public async Task With_no_stored_order_groups_keep_the_order_they_arrived_in()
    {
        var (service, _) = await LoadedAsync();

        var ordered = service.OrderGroups(new[] { Group("B"), Group("A"), Group("C") });

        Assert.Equal(new[] { "B", "A", "C" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public async Task Stored_order_wins_and_unlisted_groups_go_last()
    {
        // "Scanned" is not in the list — a group that turned up since the last drag. It must land
        // at the end rather than silently first, which is what a rank of 0 would have done.
        var (service, _) = await LoadedAsync("groupOrder: [C, A]");

        var ordered = service.OrderGroups(new[] { Group("A"), Group("Scanned"), Group("C") });

        Assert.Equal(new[] { "C", "A", "Scanned" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public async Task Order_is_matched_without_regard_to_case()
    {
        // The file is hand-editable, and nobody types ElliottElectric the same way twice.
        var (service, _) = await LoadedAsync("groupOrder: [vs code, elliottelectric]");

        var ordered = service.OrderGroups(new[] { Group("ElliottElectric"), Group("VS Code") });

        Assert.Equal(new[] { "VS Code", "ElliottElectric" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public async Task Two_groups_sharing_a_name_keep_their_relative_order()
    {
        // A saved group and a scanned group are allowed to share a name, so they share a rank.
        // The sort has to be stable or they would swap places between renders.
        var (service, _) = await LoadedAsync("groupOrder: [Shared]");

        var saved = Group("Shared", "a");
        var scanned = Group("Shared", "b");

        var ordered = service.OrderGroups(new[] { saved, scanned });

        Assert.Same(saved, ordered[0]);
        Assert.Same(scanned, ordered[1]);
    }

    [Fact]
    public async Task Moving_a_group_persists_the_whole_visible_order()
    {
        // Nothing has ever been dragged, so the stored order is empty. Writing only the two names
        // involved would leave every other group unlisted and therefore last — the drop would move
        // four other cards as a side effect.
        var (service, _) = await LoadedAsync();
        var visible = new[] { "A", "B", "C", "D" };

        await service.MoveGroupAsync("D", "B", visible);

        var layout = await service.GetAsync();
        Assert.Equal(new[] { "A", "D", "B", "C" }, layout.GroupOrder);
    }

    [Fact]
    public async Task Moving_a_group_onto_itself_changes_nothing()
    {
        var (service, storage) = await LoadedAsync();

        await service.MoveGroupAsync("A", "A", new[] { "A", "B" });

        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task Dropping_onto_a_group_that_is_no_longer_there_appends()
    {
        // The drop target can vanish mid-drag: a rescan, or a group deleted in another window.
        // Appending is the honest answer; throwing away the drag is not.
        var (service, _) = await LoadedAsync();

        await service.MoveGroupAsync("A", "Gone", new[] { "A", "B", "C" });

        var layout = await service.GetAsync();
        Assert.Equal(new[] { "B", "C", "A" }, layout.GroupOrder);
    }

    // ---- pins -------------------------------------------------------------------------------

    [Fact]
    public async Task Pinning_promotes_a_card_and_leaves_the_rest_alone()
    {
        var (service, _) = await LoadedAsync();

        Assert.True(await service.TogglePinAsync("G", "Kiosk"));

        var ordered = service.OrderWorkspaces("G", Group("G", "Account", "Checkout", "Kiosk", "Login").Workspaces);

        Assert.Equal(new[] { "Kiosk", "Account", "Checkout", "Login" }, ordered.Select(w => w.Name));
    }

    [Fact]
    public async Task Pins_survive_the_renumbering_a_rescan_does()
    {
        var (service, _) = await LoadedAsync();
        await service.TogglePinAsync("VS Code", "LoginAccess");

        // What a rescan produces: the same names, brand new negative ids.
        var rescanned = new List<Workspace>
        {
            new() { Id = -3, Name = "ees-table", GroupName = "VS Code", SourceName = "src" },
            new() { Id = -2, Name = "LoginAccess", GroupName = "VS Code", SourceName = "src" },
            new() { Id = -1, Name = "PassKeyMigration", GroupName = "VS Code", SourceName = "src" }
        };

        var ordered = service.OrderWorkspaces("VS Code", rescanned);

        Assert.Equal("LoginAccess", ordered[0].Name);
        Assert.True(service.IsPinned("VS Code", "LoginAccess"));
    }

    [Fact]
    public async Task Unpinning_the_last_card_removes_the_group_entry_entirely()
    {
        // Otherwise the file accumulates an empty list for every group ever pinned in.
        var (service, _) = await LoadedAsync();

        await service.TogglePinAsync("G", "Kiosk");
        Assert.False(await service.TogglePinAsync("G", "Kiosk"));

        var layout = await service.GetAsync();
        Assert.Empty(layout.Pinned);
        Assert.False(service.IsPinned("G", "Kiosk"));
    }

    [Fact]
    public async Task A_hand_written_pin_list_is_read_case_insensitively()
    {
        var (service, _) = await LoadedAsync("pinned:\n  elliottelectric:\n  - kiosk\n");

        Assert.True(service.IsPinned("ElliottElectric", "Kiosk"));
    }

    // ---- aliases ----------------------------------------------------------------------------

    [Fact]
    public async Task Aliases_round_trip_and_are_trimmed_and_deduplicated()
    {
        var (service, _) = await LoadedAsync();

        await service.SetAliasesAsync(AliasScope.Workspace, "InvoiceApproval",
            new[] { " invapp ", "billing", "INVAPP", "", "   " });

        Assert.Equal(new[] { "invapp", "billing" }, service.AliasesFor(AliasScope.Workspace, "InvoiceApproval"));
    }

    [Fact]
    public async Task Groups_and_workspaces_keep_separate_alias_books()
    {
        var (service, _) = await LoadedAsync();

        await service.SetAliasesAsync(AliasScope.Group, "ElliottElectric", new[] { "ee" });

        Assert.Equal(new[] { "ee" }, service.AliasesFor(AliasScope.Group, "ElliottElectric"));
        Assert.Empty(service.AliasesFor(AliasScope.Workspace, "ElliottElectric"));
    }

    [Fact]
    public async Task Clearing_the_aliases_removes_the_entry_rather_than_writing_a_blank_one()
    {
        var (service, _) = await LoadedAsync();
        await service.SetAliasesAsync(AliasScope.Workspace, "Account", new[] { "acc" });

        await service.SetAliasesAsync(AliasScope.Workspace, "Account", Array.Empty<string>());

        var layout = await service.GetAsync();
        Assert.Empty(layout.Aliases.Workspaces);
    }

    [Fact]
    public async Task Re_casing_a_name_replaces_its_aliases_instead_of_adding_a_second_entry()
    {
        var (service, _) = await LoadedAsync();
        await service.SetAliasesAsync(AliasScope.Workspace, "account", new[] { "old" });

        await service.SetAliasesAsync(AliasScope.Workspace, "Account", new[] { "new" });

        var layout = await service.GetAsync();
        Assert.Single(layout.Aliases.Workspaces);
        Assert.Equal(new[] { "new" }, service.AliasesFor(AliasScope.Workspace, "ACCOUNT"));
    }

    // ---- hidden groups -----------------------------------------------------------------------

    [Fact]
    public async Task Hiding_a_group_is_remembered_and_reversible()
    {
        var (service, _) = await LoadedAsync();

        Assert.True(await service.ToggleHiddenAsync("Old Experiments"));
        Assert.True(service.IsHidden("Old Experiments"));
        Assert.Equal(new[] { "Old Experiments" }, service.HiddenGroups);

        Assert.False(await service.ToggleHiddenAsync("Old Experiments"));
        Assert.False(service.IsHidden("Old Experiments"));

        // Removed, not left behind as an entry that happens to be false. The file only ever holds
        // groups that are actually hidden, the way the pins do.
        Assert.Empty(service.HiddenGroups);
    }

    [Fact]
    public async Task A_hidden_name_matches_whatever_case_the_file_used()
    {
        var (service, _) = await LoadedAsync("hidden: [old experiments]");

        Assert.True(service.IsHidden("Old Experiments"));

        // And unhiding by the cased name finds the lower-case entry rather than adding a second.
        Assert.False(await service.ToggleHiddenAsync("Old Experiments"));
        Assert.Empty(service.HiddenGroups);
    }

    [Fact]
    public async Task Hiding_says_nothing_about_the_group_order()
    {
        // Hiding is not deleting: a hidden group keeps its place, so unhiding it puts it back
        // where it was rather than at the end of the dashboard.
        var (service, _) = await LoadedAsync("groupOrder: [A, B, C]");

        await service.ToggleHiddenAsync("B");

        var ordered = service.OrderGroups(new[] { Group("C"), Group("B"), Group("A") });
        Assert.Equal(new[] { "A", "B", "C" }, ordered.Select(g => g.Name));
    }

    [Fact]
    public async Task Nothing_is_hidden_by_default()
    {
        var (service, _) = await LoadedAsync();

        Assert.False(service.IsHidden("Anything"));
        Assert.False(service.IsHidden(null));
        Assert.Empty(service.HiddenGroups);
    }

    // ---- degrading ---------------------------------------------------------------------------

    [Fact]
    public async Task An_unparseable_file_degrades_to_no_arrangement()
    {
        // A hand-edited file that no longer parses must not take the whole dashboard down with it.
        var (service, _) = await LoadedAsync("groupOrder: [A, B\n  this is not: yaml: at all");

        var ordered = service.OrderGroups(new[] { Group("B"), Group("A") });

        Assert.Equal(new[] { "B", "A" }, ordered.Select(g => g.Name));
        Assert.Empty(service.AliasesFor(AliasScope.Workspace, "Anything"));
    }

    [Fact]
    public async Task A_missing_file_is_not_an_error()
    {
        var (service, storage) = await LoadedAsync();

        Assert.Empty((await service.GetAsync()).GroupOrder);
        Assert.Equal(0, storage.Writes);
    }
}
