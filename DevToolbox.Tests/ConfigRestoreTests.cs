using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

public class ConfigRestoreTests : IDisposable
{
    private static readonly DateTime When = new(2026, 8, 18, 14, 30, 5, DateTimeKind.Local);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DevToolboxRestoreTests", Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_root, "bundled");
    private string Config => Path.Combine(_root, "config");

    private void Bundled(string name, string content)
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, name), content);
    }

    private void Live(string name, string content)
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, name), content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void An_identical_file_is_reported_unchanged()
    {
        Bundled("log_paths.yaml", "paths:\n  - a\n");
        Live("log_paths.yaml", "paths:\n  - a\n");

        var only = Assert.Single(ConfigRestore.Compare(Source, Config));
        Assert.Equal(ConfigRestore.State.Unchanged, only.State);
    }

    [Fact]
    public void An_edited_file_is_reported_modified()
    {
        Bundled("log_paths.yaml", "paths:\n  - a\n");
        Live("log_paths.yaml", "paths:\n  - a\n  - b   # mine\n");

        Assert.Equal(ConfigRestore.State.Modified, ConfigRestore.Compare(Source, Config)[0].State);
    }

    [Fact]
    public void A_file_the_machine_does_not_have_is_reported_missing()
    {
        Bundled("log_paths.yaml", "paths:\n");

        Assert.Equal(ConfigRestore.State.Missing, ConfigRestore.Compare(Source, Config)[0].State);
    }

    [Fact]
    public void Only_yaml_is_offered_and_the_list_is_ordered()
    {
        Bundled("zebra.yaml", "z");
        Bundled("alpha.yaml", "a");
        Bundled("readme.txt", "not config");

        var names = ConfigRestore.Compare(Source, Config).Select(c => c.Name).ToArray();

        Assert.Equal(new[] { "alpha.yaml", "zebra.yaml" }, names);
    }

    [Fact]
    public void Nothing_bundled_means_nothing_to_offer()
    {
        Assert.Empty(ConfigRestore.Compare(Path.Combine(_root, "not-here"), Config));
        Assert.Empty(ConfigRestore.Compare(null, Config));
        Assert.Empty(ConfigRestore.Compare(Source, null));
    }

    [Fact]
    public void Restoring_replaces_the_live_file_and_keeps_what_was_there()
    {
        Bundled("log_paths.yaml", "shipped");
        Live("log_paths.yaml", "mine, annotated");

        var backup = ConfigRestore.Restore("log_paths.yaml", Source, Config, When);

        Assert.Equal("shipped", File.ReadAllText(Path.Combine(Config, "log_paths.yaml")));
        Assert.NotNull(backup);
        Assert.Equal("mine, annotated", File.ReadAllText(backup!));
        Assert.EndsWith(".bak-2026-08-18-143005", backup);
    }

    [Fact]
    public void Restoring_a_file_that_is_not_there_needs_no_backup()
    {
        Bundled("log_paths.yaml", "shipped");

        Assert.Null(ConfigRestore.Restore("log_paths.yaml", Source, Config, When));
        Assert.Equal("shipped", File.ReadAllText(Path.Combine(Config, "log_paths.yaml")));
    }

    [Fact]
    public void Two_restores_in_one_day_keep_two_backups()
    {
        // Restore, edit, restore again is ordinary while experimenting. A date-only backup name
        // would let the second restore overwrite the copy the first one saved.
        Bundled("log_paths.yaml", "shipped");
        Live("log_paths.yaml", "first version");

        var one = ConfigRestore.Restore("log_paths.yaml", Source, Config, When);
        Live("log_paths.yaml", "second version");
        var two = ConfigRestore.Restore("log_paths.yaml", Source, Config, When.AddSeconds(30));

        Assert.NotEqual(one, two);
        Assert.Equal("first version", File.ReadAllText(one!));
        Assert.Equal("second version", File.ReadAllText(two!));
    }

    [Fact]
    public void Restoring_something_that_was_never_shipped_does_nothing()
    {
        Bundled("log_paths.yaml", "shipped");
        Live("mine_only.yaml", "not from the package");

        Assert.Null(ConfigRestore.Restore("mine_only.yaml", Source, Config, When));
        Assert.Equal("not from the package", File.ReadAllText(Path.Combine(Config, "mine_only.yaml")));
    }

    [Theory]
    [InlineData(@"..\escape.yaml")]
    [InlineData("sub/escape.yaml")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    public void A_name_carrying_a_path_is_refused(string name)
    {
        // Nothing legitimate needs one, and accepting one would let a restore write outside the
        // config folder.
        Bundled("log_paths.yaml", "shipped");

        Assert.Null(ConfigRestore.Restore(name, Source, Config, When));
    }
}
