using DevToolbox.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The version is what App Installer compares to decide whether to upgrade, so the four-part form
/// has to be well-formed for inputs the SDK can plausibly produce — including the ones it produces
/// only on a build machine.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Describe_keeps_the_prerelease_suffix_for_display()
    {
        var version = AppVersion.Describe("0.4.0-alpha.1", "0.4.0.0");

        Assert.Equal("0.4.0-alpha.1", version.Display);
        Assert.Equal("0.4.0.0", version.Package);
    }

    [Fact]
    public void Describe_strips_build_metadata()
    {
        // What the SDK produces once SourceRevisionId is set, which a build server does.
        var version = AppVersion.Describe("0.4.0-alpha.1+a1b2c3d4", "0.4.0.0");

        Assert.Equal("0.4.0-alpha.1", version.Display);
    }

    [Theory]
    [InlineData("0.4.0-alpha.1", AppVersion.Channel.Alpha)]
    [InlineData("0.9.2-beta.3", AppVersion.Channel.Beta)]
    [InlineData("0.9.0-BETA", AppVersion.Channel.Beta)]
    [InlineData("1.0.0", AppVersion.Channel.Release)]
    [InlineData("1.2.3", AppVersion.Channel.Release)]
    public void Describe_reads_the_channel_off_the_suffix(string informational, AppVersion.Channel expected)
    {
        Assert.Equal(expected, AppVersion.Describe(informational, "1.0.0.0").Channel);
    }

    [Fact]
    public void An_unrecognised_suffix_is_not_treated_as_a_release()
    {
        // Nothing should read as more finished than it is.
        Assert.Equal(AppVersion.Channel.Alpha, AppVersion.Describe("1.0.0-rc.1", "1.0.0.0").Channel);
    }

    [Fact]
    public void Release_builds_get_no_channel_label()
    {
        Assert.Null(AppVersion.Describe("1.0.0", "1.0.0.0").ChannelLabel);
        Assert.Equal("alpha", AppVersion.Describe("0.4.0-alpha.1", "0.4.0.0").ChannelLabel);
    }

    [Theory]
    [InlineData("0.4.0", "0.4.0.0")]
    [InlineData("0.4", "0.4.0.0")]
    [InlineData("7", "7.0.0.0")]
    [InlineData("0.4.0.0", "0.4.0.0")]
    [InlineData("0.4.0.0.9", "0.4.0.0")]
    public void Package_version_is_always_four_numeric_parts(string fileVersion, string expected)
    {
        Assert.Equal(expected, AppVersion.Describe(null, fileVersion).Package);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.x.3.4")]
    public void A_missing_or_junk_file_version_still_produces_something_MSIX_would_accept(string? fileVersion)
    {
        var package = AppVersion.Describe(null, fileVersion).Package;

        Assert.Equal(4, package.Split('.').Length);
        Assert.All(package.Split('.'), part => Assert.True(int.TryParse(part, out _)));
    }

    [Fact]
    public void With_no_informational_version_the_four_part_number_is_shown()
    {
        // Honest rather than "unknown": the number is real, it just has no channel on it.
        Assert.Equal("0.4.0.0", AppVersion.Describe(null, "0.4.0.0").Display);
    }
}
