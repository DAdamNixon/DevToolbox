using DevToolbox.Mcp.Core;
using DevToolbox.Services.Models;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// Finding which log names exist, including in a location with no <c>namePattern</c>.
/// <para>
/// That case is not hypothetical — it is the configuration on the machine this was built on, where
/// the only local location has no pattern. <c>DbLogService.DiscoverLogFileNamesAsync</c> skips such
/// locations, which is right for the UI (free text still works) and wrong for an agent, which reads
/// an empty list as "there are no logs here".
/// </para>
/// </summary>
public sealed class LogNameDiscoveryTests
{
    private static LogLocation Location(string path, string? pattern = null) =>
        new() { Name = "test", Path = path, NamePattern = pattern };

    private static void WriteFiles(string folder, params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Combine(folder, name), "x");
    }

    [Fact]
    public void A_configured_pattern_groups_by_its_name_capture()
    {
        using var temp = new TempDirectory("discovery-pattern");
        WriteFiles(temp.Path,
            "Checkout.20260821.txt", "Checkout.20260822.txt", "AccountUI.20260821.txt");

        var (names, method) = LogNameDiscovery.Discover(
            Location(temp.Path, @"^(?<name>.+)\.(?<date>\d{8})\.txt$"), ".txt");

        Assert.Equal(LogNameDiscovery.MethodPattern, method);
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Single(n => n.Name == "Checkout").FileCount);
        Assert.Equal(1, names.Single(n => n.Name == "AccountUI").FileCount);
    }

    [Fact]
    public void With_no_pattern_names_are_derived_by_stripping_a_trailing_date_stamp()
    {
        // The real local location. Without this the tool would report no log files at all.
        using var temp = new TempDirectory("discovery-heuristic");
        WriteFiles(temp.Path,
            "Checkout.20260821.txt", "Checkout.20260822.txt", "AccountUI.20260821.txt");

        var (names, method) = LogNameDiscovery.Discover(Location(temp.Path), ".txt");

        Assert.Equal(LogNameDiscovery.MethodHeuristic, method);
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Single(n => n.Name == "Checkout").FileCount);
    }

    [Fact]
    public void A_name_containing_dots_survives_the_heuristic()
    {
        // C:\inetpub\LogFiles really holds files like Account.SubAccount.Create.Time.20260721.txt.
        // Only the trailing stamp comes off; the dotted name is the name.
        using var temp = new TempDirectory("discovery-dots");
        WriteFiles(temp.Path, "Account.SubAccount.Create.Time.20260721.txt", "Checkout.WithAccount.Modern.20260821.txt");

        var (names, _) = LogNameDiscovery.Discover(Location(temp.Path), ".txt");

        Assert.Contains(names, n => n.Name == "Account.SubAccount.Create.Time");
        Assert.Contains(names, n => n.Name == "Checkout.WithAccount.Modern");
    }

    [Fact]
    public void A_file_with_no_date_stamp_keeps_its_whole_stem()
    {
        using var temp = new TempDirectory("discovery-nostamp");
        WriteFiles(temp.Path, "startup.txt");

        var (names, _) = LogNameDiscovery.Discover(Location(temp.Path), ".txt");

        Assert.Equal("startup", Assert.Single(names).Name);
    }

    [Fact]
    public void A_short_run_of_digits_is_not_mistaken_for_a_date()
    {
        // Six is the floor because yyyyMM is the shortest date stamp in real use. "Api.v2" must
        // keep its suffix, or the tool starts merging genuinely different logs into one name.
        using var temp = new TempDirectory("discovery-shortdigits");
        WriteFiles(temp.Path, "Api.2.txt", "Api.12345.txt");

        var (names, _) = LogNameDiscovery.Discover(Location(temp.Path), ".txt");

        Assert.Contains(names, n => n.Name == "Api.2");
        Assert.Contains(names, n => n.Name == "Api.12345");
    }

    [Fact]
    public void A_malformed_pattern_falls_back_rather_than_returning_nothing()
    {
        // These files are hand-edited. A broken regex should cost the pattern, not the location —
        // and the caller is told the method was heuristic so it knows what it got.
        using var temp = new TempDirectory("discovery-badpattern");
        WriteFiles(temp.Path, "Checkout.20260821.txt");

        var (names, method) = LogNameDiscovery.Discover(Location(temp.Path, "^(?<name>.+"), ".txt");

        Assert.Equal(LogNameDiscovery.MethodHeuristic, method);
        Assert.Equal("Checkout", Assert.Single(names).Name);
    }

    [Fact]
    public void Only_files_with_the_templates_extension_are_counted()
    {
        using var temp = new TempDirectory("discovery-extension");
        WriteFiles(temp.Path, "Checkout.20260821.txt", "Checkout.20260821.log");

        var (names, _) = LogNameDiscovery.Discover(Location(temp.Path), ".txt");

        Assert.Equal(1, Assert.Single(names).FileCount);
    }

    [Fact]
    public void A_location_that_is_not_there_yields_nothing_rather_than_throwing()
    {
        var (names, _) = LogNameDiscovery.Discover(
            Location(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), ".txt");

        Assert.Empty(names);
    }
}
