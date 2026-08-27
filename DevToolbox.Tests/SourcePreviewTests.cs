using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The live preview behind Scan Folders. Its whole value is that it agrees with the real scan,
/// so the tests below check a preview against a folder built on disk and then check the scan of
/// the same folder produces the same cards.
/// <para>
/// Also here: the ways a source is wrong while it is being typed. A path that does not exist
/// yet, a regex with an unclosed bracket, a pattern that matches nothing — the preview is asked
/// about all three on the way to a working source, and every one of them has to come back as a
/// sentence rather than an exception.
/// </para>
/// </summary>
public class SourcePreviewTests : IDisposable
{
    private readonly string _root;

    public SourcePreviewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DevToolboxPreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not worth failing a test run over.
        }
    }

    private void File_(string name, string content = "{}") =>
        File.WriteAllText(Path.Combine(_root, name), content);

    /// <summary>A file at a path below the root, creating the folders on the way.</summary>
    private void FileAt(string relativePath, string content = "{}")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private WorkspaceSource Source(string? nameRegex = null, string pattern = "*.code-workspace") => new()
    {
        Name = "Test source",
        Path = _root,
        Pattern = pattern,
        Scan = ScanKind.Files,
        Group = "Workspaces",
        NameRegex = nameRegex,
        DefaultLocationName = "workspace"
    };

    /// <summary>The regex that pulls a branch folder and a module out of a website path.</summary>
    private const string BranchPathRegex = @"^(?<location>[^\\]+)\\.*\\(?<workspace>[^\\]+)\.sln$";

    /// <summary>
    /// The layout the website working copies use, and the one the entry name cannot describe:
    /// the branch is a folder, so both copies of Checkout end in a file called
    /// <c>Checkout.sln</c> and nothing about either name says which branch it belongs to.
    /// </summary>
    private void BranchTree()
    {
        FileAt(@"development\wwwroot\Checkout\Checkout.sln");
        FileAt(@"demo\wwwroot\Checkout\Checkout.sln");
        FileAt(@"development\wwwroot\Login\Login.sln");
    }

    /// <summary>A recursive source over that tree, with the regex reading the path.</summary>
    private WorkspaceSource PathSource(string? nameRegex) => new()
    {
        Name = "Website",
        Path = _root,
        Pattern = "*.sln",
        Scan = ScanKind.Files,
        Recursive = true,
        Group = "Website",
        MatchOn = NameMatch.RelativePath,
        NameRegex = nameRegex,
        DefaultLocationName = "main"
    };

    /// <summary>
    /// A service with no config of its own. Preview takes the source it is given, so nothing has
    /// to be saved first — which is the point of it.
    /// </summary>
    private static WorkspaceSourceService NewService() =>
        new(new ThrowingStorage());

    /// <summary>Proves the preview never reads config: any call here is a test failure.</summary>
    private sealed class ThrowingStorage : IYamlStorageService
    {
        public string StorageDirectory => "none";
        public Task SaveAsync<T>(string fileName, T data) => throw new InvalidOperationException("preview must not save");
        public Task<T?> LoadAsync<T>(string fileName) => throw new InvalidOperationException("preview must not load");
        public Task<bool> DeleteAsync(string fileName) => throw new InvalidOperationException();
        public Task<List<string>> ListFilesAsync() => throw new InvalidOperationException();
    }

    // ---- the shape it produces --------------------------------------------------------------

    [Fact]
    public async Task A_regex_folds_related_files_into_one_card()
    {
        File_("dev-checkout.code-workspace");
        File_("demo-checkout.code-workspace");
        File_("dev-login.code-workspace");

        var preview = await NewService().PreviewAsync(
            Source(@"^(?<location>dev|demo)-(?<workspace>.+)$"));

        Assert.True(preview.PathExists);
        Assert.Null(preview.Error);
        Assert.Null(preview.RegexError);
        Assert.Equal("Workspaces", preview.GroupName);

        Assert.Equal(new[] { "checkout", "login" }, preview.Workspaces.Select(w => w.Name));
        Assert.Equal(new[] { "demo", "dev" }, preview.Workspaces[0].Locations.Select(l => l.Name));
        Assert.Equal(3, preview.EntriesFound);
        Assert.Equal(2, preview.WorkspaceCount);
        Assert.Equal(3, preview.LocationCount);
        Assert.Empty(preview.Unmatched);
    }

    [Fact]
    public async Task Without_a_regex_every_entry_is_its_own_card_and_nothing_is_flagged()
    {
        File_("alpha.code-workspace");
        File_("beta.code-workspace");

        var preview = await NewService().PreviewAsync(Source());

        Assert.Equal(new[] { "alpha", "beta" }, preview.Workspaces.Select(w => w.Name));
        Assert.All(preview.Workspaces, w => Assert.Equal("workspace", w.Locations[0].Name));

        // No regex is not a failed match. Flagging it would fill the preview with warnings about
        // the source working exactly as configured.
        Assert.Empty(preview.Unmatched);
        Assert.All(preview.Workspaces, w => Assert.True(w.Locations[0].RegexMatched));
    }

    [Fact]
    public async Task Entries_the_regex_misses_are_flagged_rather_than_hidden()
    {
        File_("dev-checkout.code-workspace");
        File_("LoginAccess.code-workspace");

        var preview = await NewService().PreviewAsync(
            Source(@"^(?<location>dev|demo)-(?<workspace>.+)$"));

        // Both are on screen — the unmatched one became a card under its whole name, which is
        // what the real scan does too. Hiding it would make the fallback look intended.
        Assert.Equal(new[] { "checkout", "LoginAccess" }, preview.Workspaces.Select(w => w.Name));
        Assert.Equal(new[] { "LoginAccess" }, preview.Unmatched);

        var fallback = preview.Workspaces.Single(w => w.Name == "LoginAccess").Locations[0];
        Assert.False(fallback.RegexMatched);
        Assert.Equal("workspace", fallback.Name);
    }

    [Fact]
    public async Task Subtitles_come_from_the_configured_json_paths()
    {
        File_("thing.code-workspace", """{ "folders": [{"path":"a"},{"path":"b"}] }""");
        File_("named.code-workspace", """{ "settings": { "description": "the one with the API" } }""");

        var source = Source();
        source.DescriptionFrom = new List<string> { "settings.description", "folders" };

        var preview = await NewService().PreviewAsync(source);

        Assert.Equal("the one with the API",
            preview.Workspaces.Single(w => w.Name == "named").Locations[0].Description);

        // An array yields a count, which is what makes a .code-workspace readable at a glance.
        Assert.Equal("2 folders",
            preview.Workspaces.Single(w => w.Name == "thing").Locations[0].Description);
    }

    [Fact]
    public async Task Directories_can_be_the_thing_collected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo-one"));
        Directory.CreateDirectory(Path.Combine(_root, "repo-two"));
        File_("ignored.code-workspace");

        var source = Source(pattern: "repo-*");
        source.Scan = ScanKind.Directories;

        var preview = await NewService().PreviewAsync(source);

        Assert.Equal(new[] { "repo-one", "repo-two" }, preview.Workspaces.Select(w => w.Name));
    }

    // ---- a branch that is a folder rather than part of the name -------------------------------

    [Fact]
    public async Task A_pattern_over_the_name_cannot_see_a_branch_that_is_a_folder()
    {
        BranchTree();

        var source = PathSource(nameRegex: null);
        source.MatchOn = NameMatch.Name;

        var preview = await NewService().PreviewAsync(source);

        // Two files called Checkout.sln fold into one card — right — but both rows come out
        // labelled "main", because the entry name is the same string for both and no regex over
        // that string could say otherwise. Nothing on the card says which branch either row is.
        // This is the case NameMatch.RelativePath exists for.
        var checkout = preview.Workspaces.Single(w => w.Name == "Checkout");
        Assert.Equal(new[] { "main", "main" }, checkout.Locations.Select(l => l.Name));
    }

    [Fact]
    public async Task A_pattern_over_the_path_splits_a_branch_that_is_a_folder()
    {
        BranchTree();

        var preview = await NewService().PreviewAsync(PathSource(BranchPathRegex));

        Assert.Null(preview.RegexError);
        Assert.Equal(new[] { "Checkout", "Login" }, preview.Workspaces.Select(w => w.Name));

        // The same fold as dev-/demo- prefixed names, off a tree where the names are identical.
        Assert.Equal(new[] { "demo", "development" },
            preview.Workspaces.Single(w => w.Name == "Checkout").Locations.Select(l => l.Name));
        Assert.Equal(new[] { "development" },
            preview.Workspaces.Single(w => w.Name == "Login").Locations.Select(l => l.Name));
        Assert.Empty(preview.Unmatched);
    }

    [Fact]
    public async Task A_miss_over_the_path_is_listed_as_the_path_the_pattern_was_given()
    {
        BranchTree();
        FileAt("Other.sln");

        var preview = await NewService().PreviewAsync(PathSource(BranchPathRegex));

        // Other.sln sits at the root with no branch folder above it. It is reported by the string
        // the regex was actually handed: listing it as "Other" would name a string the pattern
        // never saw, which is the opposite of a debuggable preview.
        Assert.Equal(new[] { "Other.sln" }, preview.Unmatched);

        // The card still falls back to the entry's own name. A relative path is not a card name.
        var fallback = preview.Workspaces.Single(w => w.Name == "Other");
        Assert.False(fallback.Locations[0].RegexMatched);
        Assert.Equal("main", fallback.Locations[0].Name);
    }

    // ---- exclusions --------------------------------------------------------------------------

    [Fact]
    public async Task An_excluded_folder_is_not_scanned()
    {
        FileAt(@"development\wwwroot\Checkout\Checkout.sln");
        FileAt(@"development\wwwroot\Checkout\obj\Debug\Checkout.sln");

        var source = PathSource(nameRegex: null);

        Assert.Equal(2, (await NewService().PreviewAsync(source)).EntriesFound);

        source.Exclude = new List<string> { "obj" };
        var preview = await NewService().PreviewAsync(source);

        Assert.Equal(1, preview.EntriesFound);
        Assert.DoesNotContain(@"\obj\", Assert.Single(preview.Workspaces).Locations[0].Path);
    }

    [Fact]
    public async Task An_exclude_drops_a_matched_file_as_well_as_a_folder()
    {
        FileAt(@"development\wwwroot\Checkout\Checkout.sln");
        FileAt(@"development\wwwroot\Checkout\Checkout.Backup.sln");

        var source = PathSource(nameRegex: null);
        source.Exclude = new List<string> { "*.Backup.sln" };

        var preview = await NewService().PreviewAsync(source);

        Assert.Equal(1, preview.EntriesFound);
        Assert.Equal(new[] { "Checkout" }, preview.Workspaces.Select(w => w.Name));
    }

    // ---- what the enumeration has to keep doing -----------------------------------------------

    [Fact]
    public async Task The_patterns_the_framework_reads_as_everything_still_mean_everything()
    {
        // Pinned because the enumeration no longer goes through Directory.EnumerateFiles, and
        // these three are the inputs where that overload does not mean what it says: '*.*' takes
        // extensionless files too, and an empty pattern is not "match nothing".
        File_("alpha.code-workspace");
        File_("readme.txt");
        File_("Makefile");

        foreach (var pattern in new[] { "*", "*.*", "" })
        {
            var preview = await NewService().PreviewAsync(Source(pattern: pattern));
            Assert.Equal(3, preview.EntriesFound);
        }
    }

    [Fact]
    public async Task A_scanned_solution_is_the_same_kind_of_thing_as_a_hand_added_one()
    {
        FileAt(@"development\wwwroot\Checkout\Checkout.sln");

        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { PathSource(nameRegex: null) }
        }));

        var group = Assert.Single(await scanning.GetGroupsAsync());
        var location = Assert.Single(Assert.Single(group.Workspaces).Locations);

        // Solution, not File — the type the Add Location dialog would have inferred for the same
        // path. A card built by a scan and one built by hand have to be interchangeable.
        Assert.Equal(LocationType.Solution, location.Type);
        Assert.Equal(Path.Combine(_root, "development", "wwwroot", "Checkout"), location.Root);
    }

    // ---- the ways a half-typed source is wrong -----------------------------------------------

    [Fact]
    public async Task A_folder_that_does_not_exist_is_reported_not_thrown()
    {
        var source = Source();
        source.Path = Path.Combine(_root, "nope", "still-nope");

        var preview = await NewService().PreviewAsync(source);

        Assert.False(preview.PathExists);
        Assert.Null(preview.Error);
        Assert.Empty(preview.Workspaces);
    }

    [Fact]
    public async Task A_path_that_is_not_a_path_yet_is_reported_not_thrown()
    {
        // What the box holds part-way through typing a UNC path or pasting one with a quote in it.
        var source = Source();
        source.Path = "\"C:\\<not a path>";

        var preview = await NewService().PreviewAsync(source);

        Assert.False(preview.PathExists);
        Assert.Empty(preview.Workspaces);
    }

    [Fact]
    public async Task An_invalid_regex_is_named_and_the_scan_carries_on_without_it()
    {
        File_("alpha.code-workspace");

        var preview = await NewService().PreviewAsync(Source("^(?<workspace>[unclosed"));

        Assert.NotNull(preview.RegexError);
        Assert.True(preview.PathExists);

        // The real scan ignores a bad regex rather than failing, which used to be
        // indistinguishable from a regex that matched nothing. The cards still appear.
        Assert.Equal(new[] { "alpha" }, preview.Workspaces.Select(w => w.Name));
        Assert.Empty(preview.Unmatched);
    }

    [Fact]
    public async Task A_pattern_matching_nothing_is_an_empty_preview_of_a_real_folder()
    {
        File_("alpha.code-workspace");

        var preview = await NewService().PreviewAsync(Source(pattern: "*.sln"));

        // PathExists true and no error: the difference between "wrong folder" and "wrong
        // pattern" is the only thing the dialog can usefully say here.
        Assert.True(preview.PathExists);
        Assert.Null(preview.Error);
        Assert.Equal(0, preview.EntriesFound);
        Assert.Empty(preview.Workspaces);
    }

    [Fact]
    public async Task A_large_folder_is_sampled_and_says_so()
    {
        // The cap is 200. A preview runs on every keystroke and a recursive '*' pointed at a
        // drive root is one keystroke away.
        for (var i = 0; i < 205; i++)
        {
            File_($"entry{i:000}.code-workspace");
        }

        var preview = await NewService().PreviewAsync(Source());

        Assert.True(preview.Truncated);
        Assert.Equal(200, preview.LocationCount);

        // 200, not 205. The walk stops at the cap instead of going on to count what is left —
        // over a recursive source that tail is thousands of folders and this runs on a keystroke.
        // Truncated is what says "there are more"; EntriesFound never claimed to be the total.
        Assert.Equal(200, preview.EntriesFound);
    }

    [Fact]
    public async Task Cancelling_a_preview_stops_it()
    {
        File_("alpha.code-workspace");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService().PreviewAsync(Source(), cts.Token));
    }

    // ---- agreement with the real scan --------------------------------------------------------

    [Fact]
    public async Task The_preview_matches_what_a_real_scan_produces()
    {
        File_("dev-checkout.code-workspace");
        File_("demo-checkout.code-workspace");
        File_("LoginAccess.code-workspace");

        var source = Source(@"^(?<location>dev|demo)-(?<workspace>.+)$");

        var preview = await NewService().PreviewAsync(source);

        // The same source, this time through GetGroupsAsync — the path the dashboard uses.
        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { source }
        }));

        var groups = await scanning.GetGroupsAsync();
        var scanned = Assert.Single(groups);

        Assert.Equal(preview.GroupName, scanned.Name);
        Assert.Equal(
            preview.Workspaces.Select(w => w.Name),
            scanned.Workspaces.Select(w => w.Name));
        Assert.Equal(
            preview.Workspaces.SelectMany(w => w.Locations).Select(l => (l.Name, l.Path)),
            scanned.Workspaces.SelectMany(w => w.Locations).Select(l => (l.Name, l.Path)));
    }

    [Fact]
    public async Task The_preview_matches_a_real_scan_over_the_path_too()
    {
        BranchTree();
        FileAt(@"development\wwwroot\Checkout\obj\Debug\Checkout.sln");

        var source = PathSource(BranchPathRegex);
        source.Exclude = new List<string> { "obj" };

        var preview = await NewService().PreviewAsync(source);

        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { source }
        }));

        var scanned = Assert.Single(await scanning.GetGroupsAsync());

        Assert.Equal(
            preview.Workspaces.Select(w => w.Name),
            scanned.Workspaces.Select(w => w.Name));
        Assert.Equal(
            preview.Workspaces.SelectMany(w => w.Locations).Select(l => (l.Name, l.Path)),
            scanned.Workspaces.SelectMany(w => w.Locations).Select(l => (l.Name, l.Path)));
    }

    [Fact]
    public async Task A_source_reports_the_cards_it_has_rows_on_not_the_ones_it_created()
    {
        // Two sources over one group, the shape the dashboard config uses: each covers a branch
        // and they fold onto each other's cards.
        FileAt(@"development\Checkout\Checkout.sln");
        FileAt(@"development\Login\Login.sln");
        FileAt(@"demo\Checkout\Checkout.sln");

        WorkspaceSource Branch(string folder, string label) => new()
        {
            Name = $"Website ({label})",
            Path = Path.Combine(_root, folder),
            Pattern = "*.sln",
            Scan = ScanKind.Files,
            Recursive = true,
            Group = "Website",
            DefaultLocationName = label
        };

        var dev = Branch("development", "dev");
        var demo = Branch("demo", "demo");

        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { dev, demo }
        }));

        await scanning.GetGroupsAsync();

        var devResult = scanning.LastScan.Single(r => r.SourceName == dev.Name);
        var demoResult = scanning.LastScan.Single(r => r.SourceName == demo.Name);

        Assert.Equal(2, devResult.WorkspacesProduced);

        // 1: the demo source carries one row, on Checkout. Counting cards a source was first to
        // create would say 0 here — dev got to Checkout first — so a working source reported
        // "0 cards", and swapping the two sources in config would have swapped the numbers
        // without anything on disk changing.
        Assert.Equal(1, demoResult.WorkspacesProduced);

        // And it agrees with the preview, which has no group to fold into and always counted the
        // cards the source itself makes. One number, one meaning, wherever it is shown.
        var preview = await scanning.PreviewAsync(demo);
        Assert.Equal(preview.WorkspaceCount, demoResult.WorkspacesProduced);
    }

    [Fact]
    public async Task A_group_records_every_source_that_fed_it()
    {
        FileAt(@"development\Checkout\Checkout.sln");
        FileAt(@"demo\Checkout\Checkout.sln");

        WorkspaceSource Branch(string folder, string label) => new()
        {
            Name = $"Website ({label})",
            Path = Path.Combine(_root, folder),
            Pattern = "*.sln",
            Scan = ScanKind.Files,
            Recursive = true,
            Group = "Website",
            DefaultLocationName = label
        };

        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { Branch("development", "dev"), Branch("demo", "demo") }
        }));

        var group = (await scanning.GetGroupsAsync()).Single();

        // Both, not just the one that got there first. SourceName is whichever created the group,
        // and a card built by four rules looked identical to one built by a single rule — which
        // is exactly the question a group of merged branch checkouts raises.
        Assert.Equal(new[] { "Website (dev)", "Website (demo)" }, group.SourceNames);
        Assert.Equal("Website (dev)", group.SourceName);
    }

    // ---- the common excludes ----------------------------------------------------------------

    /// <summary>
    /// A tree with a solution in the source and a copy of it under build output, which is the
    /// shape every one of these working copies has: <c>bin\Debug</c> holds a whole second set of
    /// everything, and the scan walked all of it.
    /// </summary>
    private void TreeWithBuildOutput()
    {
        FileAt(@"src\Checkout\Checkout.sln");
        FileAt(@"src\Checkout\bin\Debug\Checkout.sln");
        FileAt(@"src\Checkout\obj\Checkout.sln");
        FileAt(@"packages\Something\Something.sln");
        FileAt(@".git\modules\Ghost.sln");
    }

    private WorkspaceSource RecursiveSolutions() => new()
    {
        Name = "Website",
        Path = _root,
        Pattern = "*.sln",
        Scan = ScanKind.Files,
        Recursive = true,
        Group = "Website",
        DefaultLocationName = "main"
    };

    [Fact]
    public async Task Without_the_common_excludes_the_walk_picks_up_build_output()
    {
        TreeWithBuildOutput();

        var preview = await NewService().PreviewAsync(RecursiveSolutions());

        // The reason the checkbox exists. Four of these five are noise, and three of them are the
        // same solution over again — which on the dashboard is one card with four locations.
        Assert.Equal(5, preview.EntriesFound);
    }

    [Fact]
    public async Task The_common_excludes_prune_build_output_and_vcs_folders()
    {
        TreeWithBuildOutput();

        var source = RecursiveSolutions();
        source.ExcludeCommon = true;

        var preview = await NewService().PreviewAsync(source);

        Assert.Equal(1, preview.EntriesFound);
        Assert.Equal(new[] { "Checkout" }, preview.Workspaces.Select(w => w.Name));

        // And the real scan agrees with the preview, which is the only reason the preview is
        // worth having.
        var scanning = new WorkspaceSourceService(new SeededStorage(new WorkspaceSourceConfig
        {
            Sources = new List<WorkspaceSource> { source }
        }));

        var groups = await scanning.GetGroupsAsync();
        Assert.Equal(new[] { "Checkout" }, groups.Single().Workspaces.Select(w => w.Name));
    }

    [Fact]
    public async Task A_source_keeps_its_own_excludes_alongside_the_common_ones()
    {
        TreeWithBuildOutput();
        FileAt(@"src\Legacy\Legacy.sln");

        var source = RecursiveSolutions();
        source.ExcludeCommon = true;

        // The point of the checkbox: this list is now only what says something about this source.
        source.Exclude = new List<string> { "Legacy.sln" };

        var preview = await NewService().PreviewAsync(source);

        Assert.Equal(new[] { "Checkout" }, preview.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public void Naming_a_common_exclude_by_hand_as_well_does_not_double_it_up()
    {
        var source = RecursiveSolutions();
        source.ExcludeCommon = true;
        source.Exclude = new List<string> { "BIN", "Account.sln", "  " };

        var effective = source.EffectiveExcludes.ToList();

        // Case-insensitively deduplicated, and the blank is dropped — a config edited by hand
        // ends up with both spellings sooner or later, and every extra glob is checked against
        // every path segment on the way down.
        Assert.Equal(WorkspaceSource.CommonExcludes.Length + 1, effective.Count);
        Assert.Single(effective, e => e.Equals("bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Account.sln", effective);
    }

    [Fact]
    public void The_flag_is_off_by_default_so_an_older_config_scans_what_it_always_scanned()
    {
        var source = new WorkspaceSource();

        Assert.False(source.ExcludeCommon);
        Assert.Empty(source.EffectiveExcludes);
    }

    /// <summary>Hands back one config and refuses to be written to.</summary>
    private sealed class SeededStorage : IYamlStorageService
    {
        private readonly WorkspaceSourceConfig _config;

        public SeededStorage(WorkspaceSourceConfig config) => _config = config;

        public string StorageDirectory => "in-memory";

        public Task SaveAsync<T>(string fileName, T data) => throw new InvalidOperationException("not expected");

        public Task<T?> LoadAsync<T>(string fileName) =>
            Task.FromResult(_config is T typed ? typed : default);

        public Task<bool> DeleteAsync(string fileName) => throw new InvalidOperationException();

        public Task<List<string>> ListFilesAsync() => throw new InvalidOperationException();
    }
}
