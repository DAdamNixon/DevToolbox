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
        Assert.Equal(205, preview.EntriesFound);
        Assert.Equal(200, preview.LocationCount);
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
