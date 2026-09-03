using DevToolbox.Mcp.Core;
using DevToolbox.Services.Models;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The server's own YAML storage. The interesting property is <b>compatibility</b>: it reads and
/// writes the same files the DevToolbox UI does, so a mismatch in serializer settings would not
/// throw — it would silently parse a hand-written config into defaults, or rewrite the dev's file
/// in a shape their app no longer reads.
/// </summary>
public sealed class McpYamlStorageTests
{
    [Fact]
    public async Task A_missing_file_loads_as_nothing_rather_than_throwing()
    {
        // A config a machine has not created yet is an empty config, exactly as in the UI.
        using var temp = new TempDirectory("mcp-yaml-missing");
        var storage = new McpYamlStorage(temp.Path);

        Assert.Null(await storage.LoadAsync<LogTemplate>("not_there"));
    }

    [Fact]
    public async Task It_reads_the_camelCase_shape_the_ui_writes()
    {
        // The naming convention has to match YamlStorageService's exactly. If it did not, this
        // would deserialize into a template with no columns and no error anywhere.
        using var temp = new TempDirectory("mcp-yaml-shape");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "WebsiteBase.yaml"), """
            name: "WebsiteBase"
            extension: ".txt"
            delimiter: "|"
            columns:
              - DateTime
              - Guid
            sort:
              - column: DateTime
                direction: asc
            """);

        var template = await new McpYamlStorage(temp.Path).LoadAsync<LogTemplate>("WebsiteBase");

        Assert.NotNull(template);
        Assert.Equal("WebsiteBase", template!.Name);
        Assert.Equal("|", template.Delimiter);
        Assert.Equal(new[] { "DateTime", "Guid" }, template.Columns);
        Assert.Equal("DateTime", Assert.Single(template.Sort!).Column);
    }

    [Fact]
    public async Task An_unknown_key_is_ignored_because_these_files_are_hand_edited()
    {
        using var temp = new TempDirectory("mcp-yaml-unknown");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "t.yaml"), """
            name: "T"
            somethingSomeoneAdded: true
            columns: [A]
            """);

        var template = await new McpYamlStorage(temp.Path).LoadAsync<LogTemplate>("t");

        Assert.Equal("T", template!.Name);
    }

    [Fact]
    public async Task A_saved_file_round_trips()
    {
        using var temp = new TempDirectory("mcp-yaml-roundtrip");
        var storage = new McpYamlStorage(temp.Path);

        await storage.SaveAsync("saved_queries", new SavedQueryConfig
        {
            Queries = { new SavedQuery { Id = "1", Name = "n", Group = "g", Sql = "SELECT 1" } }
        });

        var loaded = await storage.LoadAsync<SavedQueryConfig>("saved_queries");

        Assert.Equal("SELECT 1", Assert.Single(loaded!.Queries).Sql);
    }

    [Fact]
    public async Task Saving_over_an_existing_file_keeps_one_backup()
    {
        // Serialization keeps neither comments nor key order, so a save rewrites a hand-annotated
        // config into bare generated YAML — and here the save was requested by an agent, not by the
        // person whose comments those were. One generation back is what makes that recoverable.
        using var temp = new TempDirectory("mcp-yaml-backup");
        var storage = new McpYamlStorage(temp.Path);
        var path = Path.Combine(temp.Path, "saved_queries.yaml");

        await File.WriteAllTextAsync(path, "# a comment the dev wrote\nqueries: []\n");
        await storage.SaveAsync("saved_queries", new SavedQueryConfig());

        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("a comment the dev wrote", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task Saving_leaves_no_temp_file_behind()
    {
        using var temp = new TempDirectory("mcp-yaml-temp");
        await new McpYamlStorage(temp.Path).SaveAsync("saved_queries", new SavedQueryConfig());

        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Delete_is_not_available_at_all()
    {
        // Guardrail #1: the absence of a code path. No tool has any business removing a config file,
        // so the capability does not exist rather than going unused.
        using var temp = new TempDirectory("mcp-yaml-delete");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => new McpYamlStorage(temp.Path).DeleteAsync("saved_queries"));
    }

    [Fact]
    public void The_default_directory_is_the_folder_the_ui_uses()
    {
        // The agent should see the same templates, locations and saved queries the dev sees. A
        // separate folder would produce a server that works perfectly against nothing.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox", "Config");

        Assert.Equal(expected, McpYamlStorage.DefaultStorageDirectory);
    }

    [Fact]
    public async Task Listing_a_directory_that_is_not_there_yields_nothing()
    {
        var storage = new McpYamlStorage(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Empty(await storage.ListFilesAsync());
    }
}
