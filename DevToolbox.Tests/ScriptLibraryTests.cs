using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// Seeding runs on every start and after every upgrade, so the property that matters is what it does
/// to a machine that already has files — not what it does to an empty one.
/// </summary>
public class ScriptLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DevToolboxScriptTests", Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_root, "bundled");
    private string Target => Path.Combine(_root, "user");

    private void Bundle(string name, string content)
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, name), content);
    }

    private void Existing(string name, string content)
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, name), content);
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(Target, name));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Shipped_scripts_are_copied_to_a_machine_that_has_none()
    {
        Bundle("npm-install.ps1", "# install");
        Bundle("Real-Clean.ps1", "# clean");

        Assert.Equal(2, ScriptLibrary.SeedFrom(Source, Target));
        Assert.Equal("# install", Read("npm-install.ps1"));
    }

    [Fact]
    public void An_edited_script_is_never_overwritten()
    {
        // The whole point. Seeding runs again on every upgrade, and a script the user has changed is
        // theirs — putting the shipped copy back would be silent data loss.
        Bundle("Real-Clean.ps1", "# shipped");
        Existing("Real-Clean.ps1", "# mine, hand-edited");

        Assert.Equal(0, ScriptLibrary.SeedFrom(Source, Target));
        Assert.Equal("# mine, hand-edited", Read("Real-Clean.ps1"));
    }

    [Fact]
    public void Seeding_is_idempotent()
    {
        Bundle("npm-install.ps1", "# install");

        Assert.Equal(1, ScriptLibrary.SeedFrom(Source, Target));
        Assert.Equal(0, ScriptLibrary.SeedFrom(Source, Target));
        Assert.Equal(0, ScriptLibrary.SeedFrom(Source, Target));
    }

    [Fact]
    public void A_missing_script_is_restored_while_the_others_are_left_alone()
    {
        Bundle("npm-install.ps1", "# shipped install");
        Bundle("Real-Clean.ps1", "# shipped clean");
        Existing("Real-Clean.ps1", "# mine");

        Assert.Equal(1, ScriptLibrary.SeedFrom(Source, Target));
        Assert.Equal("# shipped install", Read("npm-install.ps1"));
        Assert.Equal("# mine", Read("Real-Clean.ps1"));
    }

    [Fact]
    public void Only_ps1_files_are_taken()
    {
        Bundle("npm-install.ps1", "# install");
        Bundle("notes.txt", "not a script");
        Bundle("config.yaml", "not a script either");

        Assert.Equal(1, ScriptLibrary.SeedFrom(Source, Target));
        Assert.False(File.Exists(Path.Combine(Target, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(Target, "config.yaml")));
    }

    [Fact]
    public void The_extension_match_is_case_insensitive()
    {
        Bundle("Workspace-Builder.PS1", "# build");

        Assert.Equal(1, ScriptLibrary.SeedFrom(Source, Target));
    }

    [Fact]
    public void No_bundled_folder_is_not_a_failure()
    {
        // A plain build or a clone has no Scripts folder to copy from, and must still start.
        Assert.Equal(0, ScriptLibrary.SeedFrom(Path.Combine(_root, "nothing-here"), Target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_path_is_not_a_failure(string? path)
    {
        Assert.Equal(0, ScriptLibrary.SeedFrom(path!, Target));
        Assert.Equal(0, ScriptLibrary.SeedFrom(Source, path!));
    }

    [Fact]
    public void The_user_directory_is_beside_the_configuration_not_beside_the_executable()
    {
        // The bug this whole change exists for: an installed package's own folder is read-only.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "DevToolbox", "Scripts"), ScriptLibrary.UserDirectory);
        Assert.NotEqual(ScriptLibrary.BundledDirectory, ScriptLibrary.UserDirectory);
    }
}
