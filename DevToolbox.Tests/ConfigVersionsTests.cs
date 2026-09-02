using System;
using System.IO;
using System.Linq;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// Listing what a config file can be compared against, and putting one of those versions back.
/// <para>
/// Real files in a temp directory rather than an abstraction: what is being tested is the reading
/// of a folder that two other pieces of code have been writing into, in two different naming
/// shapes, for as long as this app has existed. A fake store would only assert that this class
/// agrees with itself.
/// </para>
/// </summary>
public class ConfigVersionsTests
{
    private const string Name = "log_paths.yaml";

    private static void Write(string directory, string fileName, string content) =>
        File.WriteAllText(Path.Combine(directory, fileName), content);

    [Fact]
    public void An_empty_config_folder_offers_only_the_shipped_copy()
    {
        using var shipped = new TempDirectory("cfgv-shipped");
        using var config = new TempDirectory("cfgv-config");
        Write(shipped.Path, Name, "shipped: true");

        var versions = ConfigVersions.For(Name, shipped.Path, config.Path);

        var only = Assert.Single(versions);
        Assert.Equal(ConfigVersions.Origin.Shipped, only.Origin);
    }

    [Fact]
    public void A_build_with_no_bundled_config_offers_nothing_when_there_are_no_backups()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name, "live: true");

        Assert.Empty(ConfigVersions.For(Name, sourceDirectory: null, config.Path));
    }

    /// <summary>
    /// The two shapes already on disk: <c>YamlStorageService</c>'s single undated <c>.bak</c>, and
    /// <c>ConfigRestore</c>'s dated ones. Both have to be offered, or half the history is invisible.
    /// </summary>
    [Fact]
    public void Both_backup_shapes_are_listed()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name, "live: true");
        Write(config.Path, Name + ".bak", "before the last save");
        Write(config.Path, Name + ".bak-2026-08-01-101500", "before a restore");

        var versions = ConfigVersions.For(Name, sourceDirectory: null, config.Path);

        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.Equal(ConfigVersions.Origin.Backup, v.Origin));
        Assert.Contains(versions, v => v.FileName == Name + ".bak");
        Assert.Contains(versions, v => v.FileName == Name + ".bak-2026-08-01-101500");
    }

    [Fact]
    public void A_dated_backup_takes_its_date_from_its_name()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name + ".bak-2026-08-01-101500", "x");

        var version = Assert.Single(ConfigVersions.For(Name, null, config.Path));

        Assert.Equal(new DateTime(2026, 8, 1, 10, 15, 0), version.Taken);
    }

    [Fact]
    public void Shipped_comes_first_and_backups_follow_newest_first()
    {
        using var shipped = new TempDirectory("cfgv-shipped");
        using var config = new TempDirectory("cfgv-config");
        Write(shipped.Path, Name, "shipped: true");
        Write(config.Path, Name + ".bak-2026-07-01-090000", "older");
        Write(config.Path, Name + ".bak-2026-08-15-090000", "newer");

        var versions = ConfigVersions.For(Name, shipped.Path, config.Path);

        Assert.Equal(ConfigVersions.Origin.Shipped, versions[0].Origin);
        Assert.Equal(Name + ".bak-2026-08-15-090000", versions[1].FileName);
        Assert.Equal(Name + ".bak-2026-07-01-090000", versions[2].FileName);
    }

    /// <summary>
    /// A neighbouring file's backups must not appear under this one. <c>log_paths.yaml</c> and
    /// <c>log_paths_extra.yaml</c> both start with the same characters, and the enumeration is a
    /// prefix match.
    /// </summary>
    [Fact]
    public void Backups_of_a_similarly_named_file_are_not_listed()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name + ".bak", "mine");
        Write(config.Path, "log_paths_extra.yaml.bak", "someone else's");

        var version = Assert.Single(ConfigVersions.For(Name, null, config.Path));
        Assert.Equal(Name + ".bak", version.FileName);
    }

    [Fact]
    public void A_file_that_merely_starts_with_bak_is_not_offered()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name + ".bakery", "not a backup");

        Assert.Empty(ConfigVersions.For(Name, null, config.Path));
    }

    [Fact]
    public void ReadText_returns_null_for_a_file_that_is_not_there()
    {
        using var config = new TempDirectory("cfgv-config");
        Assert.Null(ConfigVersions.ReadText(Path.Combine(config.Path, "nope.yaml")));
        Assert.Null(ConfigVersions.ReadText(null));
    }

    [Fact]
    public void Restoring_replaces_the_live_file()
    {
        using var shipped = new TempDirectory("cfgv-shipped");
        using var config = new TempDirectory("cfgv-config");
        Write(shipped.Path, Name, "shipped: true");
        Write(config.Path, Name, "mine: true");

        var version = Assert.Single(ConfigVersions.For(Name, shipped.Path, config.Path));
        ConfigVersions.RestoreFrom(version, Name, config.Path, new DateTime(2026, 9, 2, 12, 0, 0));

        Assert.Equal("shipped: true", File.ReadAllText(Path.Combine(config.Path, Name)));
    }

    /// <summary>
    /// The rule that makes the whole thing safe to click. Restoring *from a backup* is still a
    /// destructive write to the live file — someone comparing two backups to choose between them
    /// must not lose what they started with by picking the wrong one first.
    /// </summary>
    [Fact]
    public void Restoring_from_a_backup_backs_the_live_file_up_first()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name, "current work");
        Write(config.Path, Name + ".bak-2026-08-01-101500", "an older idea");

        var version = Assert.Single(ConfigVersions.For(Name, null, config.Path));
        var backup = ConfigVersions.RestoreFrom(version, Name, config.Path, new DateTime(2026, 9, 2, 12, 30, 15));

        Assert.NotNull(backup);
        Assert.Equal(Name + ".bak-2026-09-02-123015", Path.GetFileName(backup));
        Assert.Equal("current work", File.ReadAllText(backup!));
        Assert.Equal("an older idea", File.ReadAllText(Path.Combine(config.Path, Name)));
    }

    [Fact]
    public void Restoring_when_there_is_no_live_file_writes_it_and_reports_no_backup()
    {
        using var shipped = new TempDirectory("cfgv-shipped");
        using var config = new TempDirectory("cfgv-config");
        Write(shipped.Path, Name, "shipped: true");

        var version = Assert.Single(ConfigVersions.For(Name, shipped.Path, config.Path));
        var backup = ConfigVersions.RestoreFrom(version, Name, config.Path, DateTime.Now);

        Assert.Null(backup);
        Assert.Equal("shipped: true", File.ReadAllText(Path.Combine(config.Path, Name)));
    }

    /// <summary>
    /// File.Copy with overwrite truncates the destination before reading the source, so a version
    /// whose path *is* the live file would empty it. Refused rather than attempted.
    /// </summary>
    [Fact]
    public void Restoring_a_file_onto_itself_is_refused_and_changes_nothing()
    {
        using var config = new TempDirectory("cfgv-config");
        Write(config.Path, Name, "the only copy");

        var self = new ConfigVersions.Version(
            ConfigVersions.Origin.Backup, "itself", Name, Path.Combine(config.Path, Name), null);

        Assert.Null(ConfigVersions.RestoreFrom(self, Name, config.Path, DateTime.Now));
        Assert.Equal("the only copy", File.ReadAllText(Path.Combine(config.Path, Name)));
    }

    [Theory]
    [InlineData("../escape.yaml")]
    [InlineData("sub/escape.yaml")]
    [InlineData("sub\\escape.yaml")]
    public void A_name_carrying_a_path_is_refused(string name)
    {
        using var config = new TempDirectory("cfgv-config");

        Assert.Empty(ConfigVersions.For(name, null, config.Path));

        var version = new ConfigVersions.Version(
            ConfigVersions.Origin.Shipped, "x", name, Path.Combine(config.Path, "whatever"), null);
        Assert.Null(ConfigVersions.RestoreFrom(version, name, config.Path, DateTime.Now));
    }

    [Fact]
    public void A_null_version_restores_nothing()
    {
        using var config = new TempDirectory("cfgv-config");
        Assert.Null(ConfigVersions.RestoreFrom(null, Name, config.Path, DateTime.Now));
    }

    /// <summary>
    /// End to end against the class that actually writes the dated backups, so the two agree on the
    /// naming rather than each agreeing with its own test.
    /// </summary>
    [Fact]
    public void A_backup_written_by_ConfigRestore_is_listed_and_restorable()
    {
        using var shipped = new TempDirectory("cfgv-shipped");
        using var config = new TempDirectory("cfgv-config");
        Write(shipped.Path, Name, "shipped: true");
        Write(config.Path, Name, "hand edited");

        ConfigRestore.Restore(Name, shipped.Path, config.Path, new DateTime(2026, 9, 2, 8, 0, 0));
        Assert.Equal("shipped: true", File.ReadAllText(Path.Combine(config.Path, Name)));

        var backup = Assert.Single(
            ConfigVersions.For(Name, shipped.Path, config.Path), v => v.Origin == ConfigVersions.Origin.Backup);

        Assert.Equal(new DateTime(2026, 9, 2, 8, 0, 0), backup.Taken);

        ConfigVersions.RestoreFrom(backup, Name, config.Path, new DateTime(2026, 9, 2, 9, 0, 0));
        Assert.Equal("hand edited", File.ReadAllText(Path.Combine(config.Path, Name)));
    }
}
