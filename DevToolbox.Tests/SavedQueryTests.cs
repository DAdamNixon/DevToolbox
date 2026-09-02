using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// Saving, recalling and grouping the Log Viewer's advanced-mode queries.
/// <para>
/// The risk here is not any single write — it is that the group is a denormalised string on
/// every row, so "rename a group" is an N-row rewrite that has to be all-or-nothing, and two
/// queries can silently end up indistinguishable in the picker they are chosen from.
/// </para>
/// <para>
/// The fake store round-trips through the real serializer rather than holding objects, for the
/// same reason <see cref="LogConfigTests"/> does: the camelCase convention is part of the
/// contract with a hand-edited file, and a key that does not match would read back as null.
/// </para>
/// </summary>
public class SavedQueryTests
{
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

        public string Raw(string fileName) => _files[fileName];

        public bool Has(string fileName) => _files.ContainsKey(fileName);

        public void Seed(string fileName, string yaml) => _files[fileName] = yaml;

        public Task SaveAsync<T>(string fileName, T data)
        {
            _files[fileName] = Serializer.Serialize(data);
            return Task.CompletedTask;
        }

        public Task<T?> LoadAsync<T>(string fileName) =>
            Task.FromResult(_files.TryGetValue(fileName, out var yaml) ? Deserializer.Deserialize<T>(yaml) : default);

        public Task<bool> DeleteAsync(string fileName) => Task.FromResult(_files.Remove(fileName));

        public Task<List<string>> ListFilesAsync() => Task.FromResult(_files.Keys.ToList());
    }

    private static (SavedQueryService Service, FakeYamlStorage Store) Build()
    {
        var store = new FakeYamlStorage();
        return (new SavedQueryService(store), store);
    }

    private static SavedQuery New(string name, string group = "", string sql = "SELECT 1") =>
        new() { Name = name, Group = group, Sql = sql };

    [Fact]
    public async Task An_empty_store_has_no_queries()
    {
        var (service, _) = Build();

        Assert.Empty(await service.GetAllAsync());
        Assert.Empty(await service.GetGroupsAsync());
    }

    [Fact]
    public async Task Saving_assigns_an_id_and_a_timestamp()
    {
        var (service, _) = Build();

        var stored = await service.SaveAsync(New("Orders by hour", "Checkout"));

        Assert.NotEqual("", stored.Id);
        Assert.NotEqual(default, stored.UpdatedUtc);
    }

    [Fact]
    public async Task A_saved_query_comes_back_whole()
    {
        var (service, _) = Build();

        await service.SaveAsync(new SavedQuery
        {
            Name = "Orders by hour",
            Group = "Checkout",
            Sql = "SELECT LEFT(DateTime,13) AS Hour, COUNT(*) FROM logs GROUP BY Hour",
            Description = "Where the spikes are",
            Template = "WebsiteBase"
        });

        var query = Assert.Single(await service.GetAllAsync());
        Assert.Equal("Orders by hour", query.Name);
        Assert.Equal("Checkout", query.Group);
        Assert.Equal("Where the spikes are", query.Description);
        Assert.Equal("WebsiteBase", query.Template);
        Assert.Contains("GROUP BY Hour", query.Sql);
    }

    /// <summary>The whole point of the id: a rename must not orphan the reference the page holds.</summary>
    [Fact]
    public async Task Renaming_a_query_keeps_its_id()
    {
        var (service, _) = Build();
        var first = await service.SaveAsync(New("Orders", "Checkout"));

        first.Name = "Orders by hour";
        var second = await service.SaveAsync(first);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await service.GetAllAsync());
    }

    [Fact]
    public async Task Two_queries_of_the_same_name_in_one_group_are_refused()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Orders", "Checkout"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(New("Orders", "Checkout")));

        Assert.Contains("Checkout", ex.Message);
        Assert.Single(await service.GetAllAsync());
    }

    /// <summary>Case is a display choice, not an identity — "orders" and "Orders" read as one name.</summary>
    [Fact]
    public async Task The_duplicate_check_ignores_case()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Orders", "Checkout"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(New("ORDERS", "checkout")));
    }

    [Fact]
    public async Task The_same_name_in_a_different_group_is_fine()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Errors", "Checkout"));
        await service.SaveAsync(New("Errors", "WebsiteEOD"));

        Assert.Equal(2, (await service.GetAllAsync()).Count);
    }

    [Fact]
    public async Task A_blank_name_or_blank_sql_is_refused()
    {
        var (service, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(New("   ", "Checkout")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(New("Orders", "Checkout", "  ")));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task Queries_come_back_ordered_by_group_then_name()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Zebra", "WebsiteEOD"));
        await service.SaveAsync(New("Beta", "Checkout"));
        await service.SaveAsync(New("Alpha", "Checkout"));
        await service.SaveAsync(New("Loose"));

        var names = (await service.GetAllAsync()).Select(q => q.Name).ToArray();

        // Ungrouped sorts first because its group is the empty string.
        Assert.Equal(new[] { "Loose", "Alpha", "Beta", "Zebra" }, names);
    }

    [Fact]
    public async Task Groups_exclude_the_ungrouped_ones()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Loose"));
        await service.SaveAsync(New("Orders", "Checkout"));
        await service.SaveAsync(New("Totals", "Checkout"));

        Assert.Equal(new[] { "Checkout" }, await service.GetGroupsAsync());
    }

    [Fact]
    public async Task Deleting_removes_only_that_query()
    {
        var (service, _) = Build();
        var first = await service.SaveAsync(New("Orders", "Checkout"));
        await service.SaveAsync(New("Totals", "Checkout"));

        Assert.True(await service.DeleteAsync(first.Id));

        var remaining = Assert.Single(await service.GetAllAsync());
        Assert.Equal("Totals", remaining.Name);
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error()
    {
        var (service, _) = Build();

        Assert.False(await service.DeleteAsync("nope"));
        Assert.False(await service.DeleteAsync(""));
    }

    [Fact]
    public async Task Renaming_a_group_moves_every_query_in_it()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Orders", "Checkout"));
        await service.SaveAsync(New("Totals", "Checkout"));
        await service.SaveAsync(New("Nightly", "WebsiteEOD"));

        Assert.Equal(2, await service.RenameGroupAsync("Checkout", "Cart"));

        var queries = await service.GetAllAsync();
        Assert.Equal(2, queries.Count(q => q.Group == "Cart"));
        Assert.Single(queries, q => q.Group == "WebsiteEOD");
        Assert.DoesNotContain(queries, q => q.Group == "Checkout");
    }

    [Fact]
    public async Task Renaming_a_group_onto_another_merges_it()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Orders", "Checkout"));
        await service.SaveAsync(New("Nightly", "WebsiteEOD"));

        Assert.Equal(1, await service.RenameGroupAsync("Checkout", "WebsiteEOD"));
        Assert.Equal(2, (await service.GetAllAsync()).Count(q => q.Group == "WebsiteEOD"));
    }

    /// <summary>
    /// The merge that would produce two identically drawn rows is refused outright, and nothing
    /// moves — a half-applied rename splits one group into two, which is worse than not starting.
    /// </summary>
    [Fact]
    public async Task A_merge_that_would_collide_is_refused_and_moves_nothing()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Errors", "Checkout"));
        await service.SaveAsync(New("Orders", "Checkout"));
        await service.SaveAsync(New("Errors", "WebsiteEOD"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenameGroupAsync("Checkout", "WebsiteEOD"));

        Assert.Contains("Errors", ex.Message);

        var queries = await service.GetAllAsync();
        Assert.Equal(2, queries.Count(q => q.Group == "Checkout"));
        Assert.Single(queries, q => q.Group == "WebsiteEOD");
    }

    [Fact]
    public async Task Renaming_a_group_to_itself_does_nothing()
    {
        var (service, _) = Build();
        await service.SaveAsync(New("Orders", "Checkout"));

        Assert.Equal(0, await service.RenameGroupAsync("Checkout", "Checkout"));
        Assert.Equal(0, await service.RenameGroupAsync("Nothing", "Something"));
    }

    /// <summary>
    /// These files are meant to be hand-edited, and nobody writing one by hand invents a GUID.
    /// A row without an id has to stay usable rather than disappear.
    /// </summary>
    [Fact]
    public async Task A_hand_written_query_with_no_id_still_loads_and_gets_one()
    {
        var (service, store) = Build();
        store.Seed("saved_queries", """
            queries:
              - name: Hand written
                group: Checkout
                sql: SELECT * FROM logs
            """);

        var loaded = Assert.Single(await service.GetAllAsync());
        Assert.NotEqual("", loaded.Id);

        // And the id sticks once anything saves.
        await service.SaveAsync(loaded);
        Assert.Equal(loaded.Id, Assert.Single(await service.GetAllAsync()).Id);
    }

    /// <summary>
    /// Guards the camelCase keys a hand-edited file has to use. A property renamed in C# without
    /// this test would read back as null from every file already on disk.
    /// </summary>
    [Fact]
    public async Task The_file_uses_the_keys_a_hand_edited_one_would()
    {
        var (service, store) = Build();
        await service.SaveAsync(new SavedQuery
        {
            Name = "Orders",
            Group = "Checkout",
            Sql = "SELECT 1",
            Description = "note",
            Template = "WebsiteBase"
        });

        var yaml = store.Raw("saved_queries");
        Assert.Contains("queries:", yaml);
        Assert.Contains("name: Orders", yaml);
        Assert.Contains("group: Checkout", yaml);
        Assert.Contains("sql:", yaml);
        Assert.Contains("description: note", yaml);
        Assert.Contains("template: WebsiteBase", yaml);
        Assert.Contains("updatedUtc:", yaml);
    }

    [Fact]
    public async Task Names_and_groups_are_trimmed_and_an_empty_description_becomes_null()
    {
        var (service, _) = Build();

        var stored = await service.SaveAsync(new SavedQuery
        {
            Name = "  Orders  ",
            Group = "  Checkout  ",
            Sql = "  SELECT 1  ",
            Description = "   "
        });

        Assert.Equal("Orders", stored.Name);
        Assert.Equal("Checkout", stored.Group);
        Assert.Equal("SELECT 1", stored.Sql);
        Assert.Null(stored.Description);
    }
}
