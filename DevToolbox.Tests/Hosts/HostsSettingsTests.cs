using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Settings are hand-edited YAML, so what matters is that a typo degrades rather than taking the tab
/// down, and that the dialect the parser gets is the one the file describes.
/// </summary>
public class HostsSettingsTests
{
    [Fact]
    public void The_defaults_describe_the_legacy_format()
    {
        var dialect = new HostsSettings().ToDialect();

        Assert.Equal("##", dialect.Prefix);
        Assert.Equal("key", dialect.GroupVerb);
        Assert.Equal("value", dialect.OptionVerb);
        Assert.Equal("clear", dialect.ClearVerb);
        Assert.Equal(HostsSeverityLevel.Danger, dialect.SeverityFor("warn"));
        Assert.Equal(HostsSeverityLevel.Caution, dialect.SeverityFor("web"));
        Assert.Contains(";", dialect.ParkedMarkers);

        dialect.Validate();
    }

    [Fact]
    public void A_configured_dialect_reaches_the_parser()
    {
        var settings = new HostsSettings
        {
            Annotation = new HostsAnnotationSettings
            {
                Prefix = "#@",
                GroupVerb = "group",
                OptionVerb = "env",
                ClearVerb = "reset",
            },
            SeverityFlags = new Dictionary<string, string> { ["prod"] = "danger" },
        };

        var dialect = settings.ToDialect();

        Assert.Equal("#@", dialect.Prefix);
        Assert.Equal("group", dialect.GroupVerb);
        Assert.Equal(HostsSeverityLevel.Danger, dialect.SeverityFor("prod"));
        Assert.Null(dialect.SeverityFor("warn"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_token_falls_back_to_the_default_rather_than_producing_an_unusable_dialect(string? blank)
    {
        var settings = new HostsSettings
        {
            Annotation = new HostsAnnotationSettings
            {
                Prefix = blank!,
                GroupVerb = blank!,
                OptionVerb = blank!,
                ClearVerb = blank!,
                FlagSeparator = blank!,
            },
        };

        var dialect = settings.ToDialect();

        dialect.Validate();
        Assert.Equal("##", dialect.Prefix);
    }

    /// <summary>
    /// A flag exists to draw attention, so a misspelt severity should shout rather than go quiet.
    /// </summary>
    [Fact]
    public void An_unrecognised_severity_becomes_the_most_serious_one()
    {
        var settings = new HostsSettings
        {
            SeverityFlags = new Dictionary<string, string> { ["oops"] = "not-a-level" },
        };

        Assert.Equal(HostsSeverityLevel.Danger, settings.ToDialect().SeverityFor("oops"));
    }

    [Fact]
    public void Severity_names_are_read_regardless_of_case()
    {
        var settings = new HostsSettings
        {
            SeverityFlags = new Dictionary<string, string> { ["a"] = "DANGER", ["b"] = "Caution", ["c"] = "normal" },
        };

        var dialect = settings.ToDialect();

        Assert.Equal(HostsSeverityLevel.Danger, dialect.SeverityFor("a"));
        Assert.Equal(HostsSeverityLevel.Caution, dialect.SeverityFor("b"));
        Assert.Equal(HostsSeverityLevel.Normal, dialect.SeverityFor("c"));
    }

    [Fact]
    public void A_gap_of_less_than_one_blank_line_is_corrected()
    {
        Assert.Equal(1, new HostsSettings { UnscopedGapBlankLines = 0 }.ToDialect().UnscopedGapBlankLines);
        Assert.Equal(1, new HostsSettings { UnscopedGapBlankLines = -5 }.ToDialect().UnscopedGapBlankLines);
    }

    [Fact]
    public void An_empty_parked_marker_is_dropped_so_it_cannot_park_every_line()
    {
        var settings = new HostsSettings { ParkedMarkers = ["", ";"] };

        Assert.Equal([";"], settings.ToDialect().ParkedMarkers);
    }

    // ── which file to operate on ─────────────────────────────────────────────

    [Fact]
    public void A_blank_path_means_the_system_hosts_file()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        Assert.Equal(expected, new HostsSettings().ResolveHostsPath());
        Assert.Equal(expected, new HostsSettings { HostsFilePath = "  " }.ResolveHostsPath());
    }

    /// <summary>
    /// A configurable path is what makes the write path testable against a copy instead of the real
    /// system file, so environment variables have to expand.
    /// </summary>
    [Fact]
    public void A_configured_path_is_expanded_and_made_absolute()
    {
        var settings = new HostsSettings { HostsFilePath = @"%TEMP%\hosts-test" };

        var resolved = settings.ResolveHostsPath();

        Assert.Equal(Path.Combine(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "hosts-test"), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void The_starter_settings_flush_the_dns_cache_and_name_nothing_else()
    {
        var starter = HostsSettings.CreateStarter();

        Assert.NotNull(starter.AfterApply);
        Assert.Equal("ipconfig", starter.AfterApply!.ExecutablePath);
        Assert.Equal("/flushdns", starter.AfterApply.Arguments);

        // Nothing in a seeded file may name anybody's real infrastructure.
        Assert.Null(starter.HostsFilePath);
        Assert.Null(starter.Editor);
    }
}
