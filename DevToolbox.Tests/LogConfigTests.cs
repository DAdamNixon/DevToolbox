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
/// Editing the Log Viewer's templates and locations from the UI.
/// <para>
/// The risky part is not any one file — it is that a template lives in <em>two</em> places, its own
/// YAML and the index entry naming it, and they have to stay in step. An index entry pointing at a
/// file that is gone is a template that appears in the picker and then fails to load; a file with no
/// entry is invisible. Both were previously impossible because nothing wrote these files.
/// </para>
/// <para>
/// The fake store round-trips through the real serializer rather than holding objects, because the
/// camelCase naming convention is part of the contract: a property whose YAML key does not match
/// what a hand-edited file already says would silently read back as null.
/// </para>
/// </summary>
public class LogConfigTests
{
    /// <summary>In-memory YAML, serialized exactly the way <see cref="YamlStorageService"/> does.</summary>
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

        public IEnumerable<string> FileNames => _files.Keys;

        public string Raw(string fileName) => _files[fileName];

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

    private static (LogConfigService Service, FakeYamlStorage Store) Build()
    {
        var store = new FakeYamlStorage();
        return (new LogConfigService(store), store);
    }

    private static LogTemplate Template(string name, params string[] columns) => new()
    {
        Name = name,
        Extension = ".txt",
        Delimiter = "|",
        Columns = columns.ToList()
    };

    // --- creating ---

    [Fact]
    public async Task A_new_template_is_written_and_indexed_together()
    {
        var (service, store) = Build();

        var entry = await service.SaveTemplateAsync(null, Template("Order Entry", "DateTime", "Action"));

        Assert.Equal("Order_Entry.yaml", entry.File);
        Assert.Equal("Order Entry", entry.Name);

        var indexed = await service.GetTemplatesAsync();
        Assert.Single(indexed);
        Assert.Equal("Order_Entry.yaml", indexed[0].File);

        var loaded = await service.LoadTemplateAsync(entry.File);
        Assert.Equal(new[] { "DateTime", "Action" }, loaded!.Columns);
    }

    [Fact]
    public async Task A_template_name_that_is_not_a_legal_file_name_still_gets_a_file()
    {
        var (service, _) = Build();

        var entry = await service.SaveTemplateAsync(null, Template("EE / IIS *live*", "date"));

        Assert.Equal("EE_IIS_live.yaml", entry.File);
        Assert.NotNull(await service.LoadTemplateAsync(entry.File));
    }

    [Fact]
    public async Task A_name_that_would_collide_gets_a_suffix_rather_than_overwriting()
    {
        var (service, _) = Build();

        var first = await service.SaveTemplateAsync(null, Template("Checkout", "a"));
        // Same derived stem, different template: "Check out" also sanitises to Check_out, but
        // "Checkout" again is the case that matters — the picker allows two names differing only in
        // case, and neither may land on the other's file.
        var second = await service.SaveTemplateAsync(null, Template("checkout", "b"));

        // The suffix keeps the second template's own casing; what matters is that the collision was
        // seen at all, since Windows would have treated Checkout.yaml and checkout.yaml as one file.
        Assert.Equal("Checkout.yaml", first.File);
        Assert.Equal("checkout_2.yaml", second.File);
        Assert.Equal(new[] { "a" }, (await service.LoadTemplateAsync(first.File))!.Columns);
        Assert.Equal(new[] { "b" }, (await service.LoadTemplateAsync(second.File))!.Columns);
    }

    [Fact]
    public async Task A_template_with_no_name_is_refused_before_anything_is_written()
    {
        var (service, store) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveTemplateAsync(null, Template("   ", "a")));
        Assert.Empty(store.FileNames);
    }

    // --- editing ---

    [Fact]
    public async Task Renaming_a_template_moves_the_index_entry_and_leaves_the_file_where_it_is()
    {
        var (service, _) = Build();
        var created = await service.SaveTemplateAsync(null, Template("Old Name", "a"));

        var renamed = Template("New Name", "a");
        var entry = await service.SaveTemplateAsync(created.File, renamed);

        // The file name is what `inherits` refers to, so a rename must not move it.
        Assert.Equal("Old_Name.yaml", entry.File);
        Assert.Equal("New Name", entry.Name);
        Assert.Single(await service.GetTemplatesAsync());
    }

    [Fact]
    public async Task Editing_a_template_does_not_add_a_second_index_entry()
    {
        var (service, _) = Build();
        var created = await service.SaveTemplateAsync(null, Template("Checkout", "a"));

        await service.SaveTemplateAsync(created.File, Template("Checkout", "a", "b"));
        await service.SaveTemplateAsync(created.File, Template("Checkout", "a", "b", "c"));

        Assert.Single(await service.GetTemplatesAsync());
        Assert.Equal(3, (await service.LoadTemplateAsync(created.File))!.Columns.Count);
    }

    [Fact]
    public async Task Blank_columns_and_sort_rows_left_behind_by_the_editor_are_dropped()
    {
        var (service, _) = Build();

        var template = Template("Messy", "  DateTime  ", "", "   ", "Action");
        template.Sort = new List<SortColumn>
        {
            new() { Column = "DateTime", Direction = "DESC" },
            new() { Column = "  ", Direction = "asc" }
        };

        var entry = await service.SaveTemplateAsync(null, template);
        var saved = await service.LoadTemplateAsync(entry.File);

        Assert.Equal(new[] { "DateTime", "Action" }, saved!.Columns);
        Assert.Single(saved.Sort!);
        Assert.Equal("desc", saved.Sort![0].Direction);
    }

    [Fact]
    public async Task A_template_with_nothing_to_sort_on_stores_no_sort_at_all()
    {
        var (service, _) = Build();

        var template = Template("Plain", "text");
        template.Sort = new List<SortColumn>();

        var entry = await service.SaveTemplateAsync(null, template);

        // Null, not an empty list: the ingest reads "no sort" as "use whatever the caller asked
        // for", and an empty list has to mean the same thing.
        Assert.Null((await service.LoadTemplateAsync(entry.File))!.Sort);
    }

    // --- deleting ---

    [Fact]
    public async Task Deleting_a_template_removes_both_its_entry_and_its_file()
    {
        var (service, store) = Build();
        var created = await service.SaveTemplateAsync(null, Template("Doomed", "a"));
        await service.SaveTemplateAsync(null, Template("Keeper", "b"));

        await service.DeleteTemplateAsync(created.File);

        var remaining = await service.GetTemplatesAsync();
        Assert.Single(remaining);
        Assert.Equal("Keeper", remaining[0].Name);
        Assert.DoesNotContain("Doomed", store.FileNames);
    }

    [Fact]
    public async Task Templates_inheriting_from_one_about_to_be_deleted_are_named_first()
    {
        var (service, _) = Build();
        var basis = await service.SaveTemplateAsync(null, Template("Website Base", "DateTime"));

        var child = Template("Checkout", "JobSeq");
        child.Inherits = "Website_Base";
        await service.SaveTemplateAsync(null, child);

        var dependents = await service.FindTemplatesInheritingAsync(basis.File);

        Assert.Equal(new[] { "Checkout" }, dependents);
    }

    [Fact]
    public async Task A_template_that_inherits_nothing_is_not_reported_as_a_dependent()
    {
        var (service, _) = Build();
        var basis = await service.SaveTemplateAsync(null, Template("Base", "a"));
        await service.SaveTemplateAsync(null, Template("Unrelated", "b"));

        Assert.Empty(await service.FindTemplatesInheritingAsync(basis.File));
    }

    // --- locations ---

    [Fact]
    public async Task Locations_round_trip_including_the_name_pattern()
    {
        var (service, _) = Build();

        await service.SaveLocationsAsync(new[]
        {
            new LogLocation { Name = "Live Web01", Path = @"\\web01\inetpub\LogFiles", NamePattern = @"^(?<name>.+)\.(?<date>\d{8})\.txt$" },
            new LogLocation { Name = "EOX Logs", Path = @"\\eox01\Logs" }
        });

        var loaded = await service.GetLocationsAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(@"^(?<name>.+)\.(?<date>\d{8})\.txt$", loaded[0].NamePattern);
        Assert.Null(loaded[1].NamePattern);
    }

    [Fact]
    public async Task A_row_added_and_never_filled_in_is_not_saved()
    {
        var (service, _) = Build();

        await service.SaveLocationsAsync(new[]
        {
            new LogLocation { Name = "Local", Path = @"C:\inetpub\LogFiles" },
            new LogLocation()
        });

        Assert.Single(await service.GetLocationsAsync());
    }

    [Fact]
    public async Task A_blank_name_pattern_is_stored_as_absent_rather_than_as_an_empty_string()
    {
        var (service, _) = Build();

        await service.SaveLocationsAsync(new[]
        {
            new LogLocation { Name = "Local", Path = @"C:\Logs", NamePattern = "   " }
        });

        // Discovery checks for null-or-whitespace, so "" would behave the same — but it would put a
        // namePattern line in the file that reads as a pattern someone forgot to finish.
        Assert.Null((await service.GetLocationsAsync())[0].NamePattern);
    }

    [Fact]
    public async Task Whitespace_around_a_pasted_path_is_taken_off()
    {
        var (service, _) = Build();

        await service.SaveLocationsAsync(new[]
        {
            new LogLocation { Name = "  Archived  ", Path = "  \\\\fileserver01\\LogFiles  " }
        });

        var saved = (await service.GetLocationsAsync())[0];
        Assert.Equal("Archived", saved.Name);
        Assert.Equal(@"\\fileserver01\LogFiles", saved.Path);
    }

    // --- reading what is already on disk ---

    [Fact]
    public async Task A_hand_written_index_is_read_as_it_stands()
    {
        var (service, store) = Build();
        store.Seed("log_templates_index", """
            templates:
              - name: "WebsiteBase"
                file: "WebsiteBase.yaml"
              - name: "EE IIS"
                file: "EE_IIS.yaml"
            """);

        var templates = await service.GetTemplatesAsync();

        Assert.Equal(2, templates.Count);
        Assert.Equal("EE_IIS.yaml", templates[1].File);
    }

    [Fact]
    public async Task A_config_that_is_not_there_yet_reads_as_empty_rather_than_throwing()
    {
        var (service, _) = Build();

        Assert.Empty(await service.GetTemplatesAsync());
        Assert.Empty(await service.GetLocationsAsync());
        Assert.Null(await service.LoadTemplateAsync("Nothing.yaml"));
    }

    [Fact]
    public async Task Editing_a_hand_written_template_finds_it_by_file_whether_or_not_the_extension_is_given()
    {
        var (service, store) = Build();
        store.Seed("log_templates_index", """
            templates:
              - name: "WebsiteBase"
                file: "WebsiteBase.yaml"
            """);
        store.Seed("WebsiteBase", """
            name: "WebsiteBase"
            extension: ".txt"
            delimiter: "|"
            columns:
              - DateTime
            """);

        var entry = await service.SaveTemplateAsync("WebsiteBase", Template("WebsiteBase", "DateTime", "Guid"));

        Assert.Equal("WebsiteBase.yaml", entry.File);
        Assert.Single(await service.GetTemplatesAsync());
        Assert.Equal(2, (await service.LoadTemplateAsync("WebsiteBase.yaml"))!.Columns.Count);
    }
}
