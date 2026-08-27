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

        /// <summary>The YAML as written, for asserting on what does and does not reach the file.</summary>
        public string Read(string fileName) => _files.TryGetValue(fileName, out var yaml) ? yaml : string.Empty;

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

    // ---- card overrides ----------------------------------------------------------------------
    //
    // The layer that makes a scanned card editable. Everything below is about the same property:
    // the override is a patch keyed by the name the *scan* produced, so a rescan still picks up new
    // projects and the edits reapply on top of them.

    /// <summary>A scanned group, the kind the overrides apply to.</summary>
    private static WorkspaceGroup Scanned(string name, params string[] cards) => new()
    {
        Id = -1,
        Name = name,
        SourceName = "Some smart folder",
        Workspaces = cards.Select(c => new Workspace
        {
            Id = -1,
            Name = c,
            GroupName = name,
            SourceName = "Some smart folder",
            Locations = new List<WorkspaceLocation>
            {
                new() { Name = "dev", Path = $@"C:\repo\development\{c}\{c}.sln" }
            }
        }).ToList()
    };

    [Fact]
    public async Task A_renamed_card_shows_the_new_name_and_remembers_the_old_one()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");

        var group = service.Customize(Scanned("eesnet.com", "PM.UI", "Checkout"));
        var card = group.Workspaces.Single(w => w.Name == "Personnel Manager");

        // The scanned name has to survive on the card, or renaming it once would make it impossible
        // to rename again — the override is keyed by what the scan called it.
        Assert.Equal("PM.UI", card.ScannedName);
        Assert.Equal("PM.UI", card.OverrideKey);
        Assert.Equal("Checkout", group.Workspaces.Single(w => w.Name == "Checkout").OverrideKey);
    }

    [Fact]
    public async Task Customize_never_edits_the_group_it_is_given()
    {
        // The scanned groups are held by WorkspaceSourceService and handed to every caller, and
        // this runs on every rebuild of the view models. Renaming in place would apply once and
        // then find nothing to rename on the second pass, because the key had already gone.
        var (service, _) = await LoadedAsync();
        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");

        var scanned = Scanned("eesnet.com", "PM.UI");

        var first = service.Customize(scanned);
        var second = service.Customize(scanned);

        Assert.Equal("PM.UI", scanned.Workspaces[0].Name);
        Assert.Equal("Personnel Manager", first.Workspaces[0].Name);
        Assert.Equal("Personnel Manager", second.Workspaces[0].Name);
    }

    [Fact]
    public async Task A_rescan_does_not_bring_a_renamed_card_back_alongside_its_new_name()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");
        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");

        // A rescan hands back a brand new group of brand new Workspace objects, still using the
        // names the scan produces — the file on disk has not changed, so it is still called PM.UI.
        // The override is keyed by exactly that, so it matches and relabels the one card. Keying it
        // by the *new* name is what would produce two: nothing would match PM.UI, so it would come
        // back as itself next to a Personnel Manager that no longer had anything to rename.
        for (var rescan = 0; rescan < 3; rescan++)
        {
            var group = service.Customize(Scanned("eesnet.com", "PM.UI", "PM.UI.Development", "Checkout"));

            Assert.Equal(new[] { "Checkout", "Personnel Manager" }, group.Workspaces.Select(w => w.Name));
            Assert.Equal(2, group.Workspaces.Single(w => w.Name == "Personnel Manager").Locations.Count);
        }
    }

    [Fact]
    public async Task A_card_renamed_on_disk_appears_under_its_new_scanned_name_and_not_twice()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");

        // The other direction: the file itself was renamed, so the scan no longer produces PM.UI at
        // all. The override stops matching and the card shows up as what it now is — once. This is
        // the honest cost of keying on names, and the failure is "my rename stopped applying"
        // rather than "there are two of these now".
        var group = service.Customize(Scanned("eesnet.com", "PersonnelMgmt.UI", "Checkout"));

        Assert.Equal(new[] { "Checkout", "PersonnelMgmt.UI" }, group.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public async Task Renaming_a_card_back_to_its_scanned_name_drops_the_override()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");
        await service.RenameCardAsync("eesnet.com", "PM.UI", "PM.UI");

        // Not stored as "renamed to the same thing": the card should follow the file again, so a
        // rescan that renames the file is followed rather than overridden.
        Assert.Null(service.CardOverrideFor("eesnet.com", "PM.UI"));
        Assert.False(service.HasCardOverrides("eesnet.com"));
    }

    [Fact]
    public async Task Renaming_a_card_carries_its_pin_across()
    {
        var (service, _) = await LoadedAsync();

        await service.TogglePinAsync("eesnet.com", "PM.UI");
        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");

        // The pin is keyed by what is on the card, so without this a rename silently unpins it.
        Assert.True(service.IsPinned("eesnet.com", "Personnel Manager"));
        Assert.False(service.IsPinned("eesnet.com", "PM.UI"));
    }

    [Fact]
    public async Task Merging_one_card_into_another_moves_its_locations_over()
    {
        var (service, _) = await LoadedAsync();

        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");

        var group = service.Customize(Scanned("eesnet.com", "PM.UI", "PM.UI.Development", "Checkout"));

        Assert.Equal(new[] { "Checkout", "PM.UI" }, group.Workspaces.Select(w => w.Name));

        var merged = group.Workspaces.Single(w => w.Name == "PM.UI");
        Assert.Equal(2, merged.Locations.Count);
        Assert.Contains(merged.Locations, l => l.Path.Contains("PM.UI.Development"));
    }

    [Fact]
    public async Task A_merge_whose_target_is_no_longer_scanned_gives_the_cards_back()
    {
        var (service, _) = await LoadedAsync();

        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");

        // The absorber's file was renamed, moved or deleted since. The card it was holding must
        // come back as itself: a stale merge that can eat a project is worse than no merge.
        var group = service.Customize(Scanned("eesnet.com", "PM.UI.Development", "Checkout"));

        Assert.Equal(new[] { "Checkout", "PM.UI.Development" }, group.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public async Task Merging_a_card_that_had_absorbed_others_brings_the_whole_set()
    {
        var (service, _) = await LoadedAsync();

        await service.MergeCardsAsync("eesnet.com", "B", "C");
        await service.MergeCardsAsync("eesnet.com", "A", "B");

        var group = service.Customize(Scanned("eesnet.com", "A", "B", "C"));

        Assert.Equal(new[] { "A" }, group.Workspaces.Select(w => w.Name));
        Assert.Equal(3, group.Workspaces[0].Locations.Count);

        // And B stops claiming to absorb anything, or the file would describe a card that no
        // longer appears as the owner of C.
        Assert.Null(service.CardOverrideFor("eesnet.com", "B"));
    }

    [Fact]
    public async Task Unmerging_releases_one_card_and_leaves_the_rest()
    {
        var (service, _) = await LoadedAsync();

        await service.MergeCardsAsync("eesnet.com", "A", "B");
        await service.MergeCardsAsync("eesnet.com", "A", "C");
        await service.UnmergeCardAsync("eesnet.com", "A", "B");

        var group = service.Customize(Scanned("eesnet.com", "A", "B", "C"));

        Assert.Equal(new[] { "A", "B" }, group.Workspaces.Select(w => w.Name));
        Assert.Equal(2, group.Workspaces.Single(w => w.Name == "A").Locations.Count);
    }

    [Fact]
    public async Task A_cycle_of_merges_leaves_every_card_alone_instead_of_looping()
    {
        // A hand-edited file describing something impossible: each card absorbing the other. Both
        // appear as themselves — terminating and lossless, so the mistake is visible on the
        // dashboard and nothing has been swallowed by it.
        var (service, _) = await LoadedAsync(
            "cards:\n  eesnet.com:\n    A:\n      absorb: [A, B]\n    B:\n      absorb: [A]");

        var group = service.Customize(Scanned("eesnet.com", "A", "B"));

        Assert.Equal(new[] { "A", "B" }, group.Workspaces.Select(w => w.Name));
        Assert.Equal(1, group.Workspaces[0].Locations.Count);
    }

    [Fact]
    public async Task A_hand_written_chain_of_merges_lands_everything_on_the_end_of_it()
    {
        // A absorbs B, B absorbs C. Only a hand-edited file gets here — MergeCardsAsync flattens
        // chains as it writes them — but stopping at B would look for a card that does not appear
        // and strand C on a card of its own.
        var (service, _) = await LoadedAsync(
            "cards:\n  eesnet.com:\n    A:\n      absorb: [B]\n    B:\n      absorb: [C]");

        var group = service.Customize(Scanned("eesnet.com", "A", "B", "C"));

        Assert.Equal(new[] { "A" }, group.Workspaces.Select(w => w.Name));
        Assert.Equal(3, group.Workspaces[0].Locations.Count);
    }

    [Fact]
    public async Task A_hidden_card_is_dropped_unless_it_is_asked_for()
    {
        var (service, _) = await LoadedAsync();

        await service.SetCardHiddenAsync("elliottelectric.com", "Account.UI", true);

        var scanned = Scanned("elliottelectric.com", "Account", "Account.UI");

        Assert.Equal(new[] { "Account" }, service.Customize(scanned).Workspaces.Select(w => w.Name));

        // includeHidden is what arrange mode passes, so a card you hid and forgot can be got back.
        Assert.Equal(
            new[] { "Account", "Account.UI" },
            service.Customize(scanned, includeHidden: true).Workspaces.Select(w => w.Name));

        Assert.True(service.IsCardHidden("elliottelectric.com", "Account.UI"));
    }

    [Fact]
    public async Task Location_labels_are_keyed_by_path_so_two_locations_sharing_a_name_can_differ()
    {
        var (service, _) = await LoadedAsync();

        // The situation the whole feature exists for: one branch holding two copies of the same
        // solution, so the card has two locations both called "dev" and nothing but the path to
        // tell the renamer which one it means.
        var group = new WorkspaceGroup
        {
            Id = -1,
            Name = "elliottelectric.com",
            SourceName = "s",
            Workspaces = new List<Workspace>
            {
                new()
                {
                    Id = -1, Name = "Products", SourceName = "s",
                    Locations = new List<WorkspaceLocation>
                    {
                        new() { Name = "dev", Path = @"C:\repo\dev\wwwroot\P\Products.sln" },
                        new() { Name = "dev", Path = @"C:\repo\dev\wwwroot\Products\Products.sln" }
                    }
                }
            }
        };

        await service.SetLocationNamesAsync("elliottelectric.com", "Products", new Dictionary<string, string>
        {
            [@"C:\repo\dev\wwwroot\P\Products.sln"] = "dev (P)"
        });

        var card = service.Customize(group).Workspaces.Single();

        Assert.Equal(new[] { "dev", "dev (P)" }, card.Locations.Select(l => l.Name));

        // And the scan's own object is untouched, or the label would stick until the next rescan
        // even after the override was dropped.
        Assert.Equal(new[] { "dev", "dev" }, group.Workspaces[0].Locations.Select(l => l.Name));
    }

    [Fact]
    public async Task A_label_on_a_path_that_moved_cards_still_applies()
    {
        var (service, _) = await LoadedAsync();

        await service.SetLocationNamesAsync("eesnet.com", "PM.UI.Development", new Dictionary<string, string>
        {
            [@"C:\repo\development\PM.UI.Development\PM.UI.Development.sln"] = "dev filter"
        });

        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");

        var card = service.Customize(Scanned("eesnet.com", "PM.UI", "PM.UI.Development")).Workspaces.Single();

        // Keyed by path, so the label means the same thing on the card those paths just moved to.
        Assert.Contains("dev filter", card.Locations.Select(l => l.Name));
    }

    [Fact]
    public async Task Resetting_a_group_drops_every_card_edit_in_it()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("eesnet.com", "PM.UI", "Personnel Manager");
        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");
        await service.SetCardHiddenAsync("eesnet.com", "Checkout", true);

        Assert.True(service.HasCardOverrides("eesnet.com"));

        await service.ResetCardsAsync("eesnet.com");

        Assert.False(service.HasCardOverrides("eesnet.com"));

        // Back to the scan exactly as it arrived, in the order it arrived — with no overrides left,
        // Customize hands the group straight back rather than re-sorting it.
        var scanned = Scanned("eesnet.com", "PM.UI", "PM.UI.Development", "Checkout");
        var group = service.Customize(scanned);

        Assert.Same(scanned, group);
        Assert.Equal(new[] { "PM.UI", "PM.UI.Development", "Checkout" }, group.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public async Task A_group_with_no_overrides_is_handed_straight_back()
    {
        var (service, _) = await LoadedAsync();

        var scanned = Scanned("eesnet.com", "A", "B");

        // The overwhelmingly common case, and the one that runs on every render: no copying, and —
        // for a saved group — the Workspaces list stays the one the dashboard writes back to disk.
        Assert.Same(scanned, service.Customize(scanned));
    }

    [Fact]
    public async Task An_override_that_says_nothing_is_removed_from_the_file()
    {
        var (service, storage) = await LoadedAsync();

        await service.SetCardHiddenAsync("eesnet.com", "Checkout", true);
        await service.SetCardHiddenAsync("eesnet.com", "Checkout", false);

        // The file should read as the list of things that have been changed, not as a graveyard of
        // cards that were once touched.
        Assert.False(service.HasCardOverrides("eesnet.com"));
        Assert.DoesNotContain("Checkout", storage.Read("dashboardLayout"));
    }

    [Fact]
    public async Task An_override_only_writes_the_fields_that_say_something()
    {
        var (service, storage) = await LoadedAsync();

        await service.MergeCardsAsync("eesnet.com", "PM.UI", "PM.UI.Development");

        var yaml = storage.Read("dashboardLayout");

        // A merged card used to write `name:`, `hidden: false` and `locations: {}` alongside its
        // absorb list — three lines of noise per card, in a file whose whole point is being read
        // and hand-edited.
        Assert.Contains("absorb:", yaml);
        Assert.DoesNotContain("hidden: false", yaml);
        Assert.DoesNotContain("locations: {}", yaml);
        Assert.DoesNotContain("name: \n", yaml.Replace("\r", string.Empty));

        // And it still round-trips: the omitted keys load back as the same defaults.
        var reloaded = new DashboardLayoutService(storage);
        await reloaded.GetAsync();

        var patch = reloaded.CardOverrideFor("eesnet.com", "PM.UI");
        Assert.NotNull(patch);
        Assert.Null(patch!.Name);
        Assert.False(patch.Hidden);
        Assert.Empty(patch.Locations);
        Assert.Equal(new[] { "PM.UI.Development" }, patch.Absorb);
    }

    // ---- sorting -----------------------------------------------------------------------------

    /// <summary>Cards with a controllable location count, for the Locations sort.</summary>
    private static List<Workspace> Cards(params (string Name, int Locations)[] cards) => cards
        .Select(c => new Workspace
        {
            Id = -1,
            Name = c.Name,
            Locations = Enumerable.Range(0, c.Locations)
                .Select(i => new WorkspaceLocation { Name = $"loc{i}", Path = $@"C:\x\{c.Name}\{i}" })
                .ToList()
        })
        .ToList();

    [Fact]
    public async Task Default_leaves_the_cards_exactly_as_they_arrived()
    {
        var (service, _) = await LoadedAsync();

        var cards = Cards(("Zebra", 1), ("Apple", 3), ("Mango", 2));

        // The whole reason Default exists as a value rather than being Name: adding sorting had to
        // change nobody's dashboard, and a hand-made group is in the order its file lists.
        Assert.Equal(CardSort.Default, service.SortFor("G"));
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, service.OrderWorkspaces("G", cards).Select(w => w.Name));
    }

    [Fact]
    public async Task Name_and_Locations_sort_the_way_they_say()
    {
        var (service, _) = await LoadedAsync();
        var cards = Cards(("Zebra", 1), ("Apple", 3), ("Mango", 2));

        await service.SetSortAsync("G", CardSort.Name);
        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, service.OrderWorkspaces("G", cards).Select(w => w.Name));

        await service.SetSortAsync("G", CardSort.Locations);
        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, service.OrderWorkspaces("G", cards).Select(w => w.Name));

        // Locations is most-first, so a tie has to fall back to something stable rather than to
        // whatever order the scan happened to produce.
        var tied = Cards(("Zebra", 2), ("Apple", 2), ("Solo", 5));
        Assert.Equal(new[] { "Solo", "Apple", "Zebra" }, service.OrderWorkspaces("G", tied).Select(w => w.Name));
    }

    [Fact]
    public async Task Pins_sit_on_top_of_whatever_sort_is_chosen()
    {
        var (service, _) = await LoadedAsync();
        var cards = Cards(("Apple", 3), ("Mango", 2), ("Zebra", 1));

        await service.SetSortAsync("G", CardSort.Name);
        await service.TogglePinAsync("G", "Zebra");

        // A pin is a promotion out of the order, not a different order — so Zebra leads and the
        // rest stay alphabetical behind it.
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, service.OrderWorkspaces("G", cards).Select(w => w.Name));
    }

    [Fact]
    public async Task Choosing_Custom_starts_from_what_is_on_screen()
    {
        var (service, _) = await LoadedAsync();
        var cards = Cards(("Zebra", 1), ("Apple", 3), ("Mango", 2));

        await service.SetSortAsync("G", CardSort.Custom, new[] { "Zebra", "Apple", "Mango" });

        // Without the seed, the first thing "Custom" does is scramble the group into whatever order
        // the scan handed over — which reads as the option being broken rather than as an empty
        // custom order.
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, service.CardOrderFor("G"));
        Assert.Equal(new[] { "Zebra", "Apple", "Mango" }, service.OrderWorkspaces("G", cards).Select(w => w.Name));
    }

    [Fact]
    public async Task Choosing_Custom_again_does_not_overwrite_an_order_already_arranged()
    {
        var (service, _) = await LoadedAsync();

        await service.SetSortAsync("G", CardSort.Custom, new[] { "A", "B", "C" });
        await service.MoveCardAsync("G", "C", "A", new[] { "A", "B", "C" });
        Assert.Equal(new[] { "C", "A", "B" }, service.CardOrderFor("G"));

        // Switching away and back is a common thing to do while comparing orders, and it must not
        // throw away the arrangement.
        await service.SetSortAsync("G", CardSort.Name);
        await service.SetSortAsync("G", CardSort.Custom, new[] { "A", "B", "C" });

        Assert.Equal(new[] { "C", "A", "B" }, service.CardOrderFor("G"));
    }

    [Fact]
    public async Task Dragging_a_card_switches_the_group_to_Custom()
    {
        var (service, _) = await LoadedAsync();

        // Dragging a card *is* choosing a custom order. Storing the order without the mode would
        // write something nothing reads and appear to do nothing at all.
        await service.MoveCardAsync("G", "C", "A", new[] { "A", "B", "C" });

        Assert.Equal(CardSort.Custom, service.SortFor("G"));
        Assert.Equal(new[] { "C", "A", "B" }, service.CardOrderFor("G"));
    }

    [Fact]
    public async Task A_card_dropped_past_the_last_one_goes_to_the_end()
    {
        var (service, _) = await LoadedAsync();

        // Last is the position a drop-before rule cannot express, so the dialog renders a target
        // below the last row and it arrives here as an unknown target.
        await service.MoveCardAsync("G", "A", string.Empty, new[] { "A", "B", "C" });

        Assert.Equal(new[] { "B", "C", "A" }, service.CardOrderFor("G"));
    }

    [Fact]
    public async Task A_card_that_is_not_in_the_custom_order_sorts_after_the_ones_that_are()
    {
        var (service, _) = await LoadedAsync();

        await service.SetSortAsync("G", CardSort.Custom, new[] { "Zebra", "Apple" });

        // A project scanned since the last arranging. It turns up at the end rather than silently
        // first, and the ones that were arranged keep their order.
        var cards = Cards(("Apple", 1), ("Newcomer", 1), ("Zebra", 1));

        Assert.Equal(
            new[] { "Zebra", "Apple", "Newcomer" },
            service.OrderWorkspaces("G", cards).Select(w => w.Name));
    }

    [Fact]
    public async Task Renaming_a_card_carries_its_place_in_the_custom_order()
    {
        var (service, _) = await LoadedAsync();

        await service.SetSortAsync("G", CardSort.Custom, new[] { "A", "PM.UI", "C" });
        await service.RenameCardAsync("G", "PM.UI", "Personnel Manager");

        // The order is a list of card names too, so a rename that did not move it would drop the
        // card to the end of its own group.
        Assert.Equal(new[] { "A", "Personnel Manager", "C" }, service.CardOrderFor("G"));
    }

    [Fact]
    public async Task Resetting_the_arrangement_leaves_the_card_edits_alone()
    {
        var (service, _) = await LoadedAsync();

        await service.RenameCardAsync("G", "PM.UI", "Personnel Manager");
        await service.MoveCardAsync("G", "C", "A", new[] { "A", "B", "C" });

        await service.ResetArrangementAsync("G");

        // "Put the order back" and "undo my renames and merges" are different regrets. One button
        // for both would make the safe one scary.
        Assert.Equal(CardSort.Default, service.SortFor("G"));
        Assert.Empty(service.CardOrderFor("G"));
        Assert.Equal("Personnel Manager", service.CardOverrideFor("G", "PM.UI")?.Name);
    }

    [Fact]
    public async Task Default_is_not_written_to_the_file()
    {
        var (service, storage) = await LoadedAsync();

        await service.SetSortAsync("G", CardSort.Name);
        await service.SetSortAsync("G", CardSort.Default);

        Assert.DoesNotContain("Default", storage.Read("dashboardLayout"));
        Assert.Equal(CardSort.Default, service.SortFor("G"));
    }

    // ---- renaming a group --------------------------------------------------------------------

    [Fact]
    public async Task Renaming_a_group_carries_its_whole_arrangement_across()
    {
        var (service, _) = await LoadedAsync("groupOrder: [Alpha, Beta, Gamma]");

        await service.TogglePinAsync("Beta", "Checkout");
        await service.ToggleHiddenAsync("Beta");
        await service.SetAliasesAsync(AliasScope.Group, "Beta", new[] { "bee" });
        await service.RenameCardAsync("Beta", "PM.UI", "Personnel Manager");
        await service.SetSortAsync("Beta", CardSort.Custom, new[] { "Checkout", "Personnel Manager" });

        await service.RenameGroupAsync("Beta", "Delta");

        // Every one of these is keyed by group name. Before this existed, renaming a group threw
        // the lot away and nothing said so.
        var layout = await service.GetAsync();
        Assert.Equal(new[] { "Alpha", "Delta", "Gamma" }, layout.GroupOrder);
        Assert.True(service.IsPinned("Delta", "Checkout"));
        Assert.True(service.IsHidden("Delta"));
        Assert.Equal(new[] { "bee" }, service.AliasesFor(AliasScope.Group, "Delta"));
        Assert.Equal("Personnel Manager", service.CardOverrideFor("Delta", "PM.UI")?.Name);
        Assert.Equal(CardSort.Custom, service.SortFor("Delta"));
        Assert.Equal(new[] { "Checkout", "Personnel Manager" }, service.CardOrderFor("Delta"));

        Assert.False(service.IsPinned("Beta", "Checkout"));
        Assert.False(service.IsHidden("Beta"));
        Assert.Null(service.CardOverrideFor("Beta", "PM.UI"));
        Assert.Equal(CardSort.Default, service.SortFor("Beta"));
        Assert.Empty(service.CardOrderFor("Beta"));
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
