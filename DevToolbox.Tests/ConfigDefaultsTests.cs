using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// The step that lets a package arrive configured.
/// <para>
/// Tested mainly for what it must <em>not</em> do. An installer runs again on every upgrade and
/// every repair, and these files are hand-edited and commented — so the one unacceptable outcome is
/// a reinstall quietly replacing a developer's config with a shipped default.
/// </para>
/// </summary>
public class ConfigDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DevToolboxSeed", Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_root, "ConfigDefaults");

    private string Config => Path.Combine(_root, "Config");

    public ConfigDefaultsTests()
    {
        Directory.CreateDirectory(Source);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void GivenBundled(string name, string content) =>
        File.WriteAllText(Path.Combine(Source, name), content);

    private void GivenExisting(string name, string content)
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, name), content);
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(Config, name));

    [Fact]
    public void A_bundled_file_the_machine_does_not_have_is_copied()
    {
        GivenBundled("log_paths.yaml", "locations: []");

        Assert.Equal(1, ConfigDefaults.SeedFrom(Source, Config));
        Assert.Equal("locations: []", Read("log_paths.yaml"));
    }

    [Fact]
    public void A_file_that_already_exists_is_left_exactly_as_it_was()
    {
        GivenExisting("log_paths.yaml", "# mine, hand-edited");
        GivenBundled("log_paths.yaml", "# the shipped default");

        Assert.Equal(0, ConfigDefaults.SeedFrom(Source, Config));
        Assert.Equal("# mine, hand-edited", Read("log_paths.yaml"));
    }

    [Fact]
    public void The_files_the_machine_is_missing_are_seeded_without_touching_the_ones_it_has()
    {
        GivenExisting("workspaceGroups.yaml", "# mine");
        GivenBundled("workspaceGroups.yaml", "# shipped");
        GivenBundled("openHandlers.yaml", "# shipped");

        Assert.Equal(1, ConfigDefaults.SeedFrom(Source, Config));
        Assert.Equal("# mine", Read("workspaceGroups.yaml"));
        Assert.Equal("# shipped", Read("openHandlers.yaml"));
    }

    [Fact]
    public void Only_yaml_is_seeded()
    {
        // A backup left in the source folder is the likely accident, and .bak is not config.
        GivenBundled("log_paths.yaml", "yes");
        GivenBundled("log_paths.yaml.bak", "no");
        GivenBundled("notes.txt", "no");
        GivenBundled("logs.db", "no");

        Assert.Equal(1, ConfigDefaults.SeedFrom(Source, Config));
        Assert.True(File.Exists(Path.Combine(Config, "log_paths.yaml")));
        Assert.False(File.Exists(Path.Combine(Config, "log_paths.yaml.bak")));
        Assert.False(File.Exists(Path.Combine(Config, "notes.txt")));
    }

    [Fact]
    public void The_extension_is_matched_whatever_its_case()
    {
        GivenBundled("Checkout.YAML", "yes");

        Assert.Equal(1, ConfigDefaults.SeedFrom(Source, Config));
        Assert.True(File.Exists(Path.Combine(Config, "Checkout.YAML")));
    }

    [Fact]
    public void The_config_folder_is_created_when_it_is_not_there()
    {
        GivenBundled("ui_settings.yaml", "theme: dark");
        Assert.False(Directory.Exists(Config));

        Assert.Equal(1, ConfigDefaults.SeedFrom(Source, Config));
        Assert.True(File.Exists(Path.Combine(Config, "ui_settings.yaml")));
    }

    [Fact]
    public void No_bundled_folder_is_the_ordinary_case_and_does_nothing()
    {
        // A plain build or a clone has no ConfigDefaults beside the executable. Seeding has to be a
        // no-op there, not a startup failure.
        Assert.Equal(0, ConfigDefaults.SeedFrom(Path.Combine(_root, "NotThere"), Config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_does_nothing(string blank)
    {
        Assert.Equal(0, ConfigDefaults.SeedFrom(blank, Config));
        Assert.Equal(0, ConfigDefaults.SeedFrom(Source, blank));
    }
}
