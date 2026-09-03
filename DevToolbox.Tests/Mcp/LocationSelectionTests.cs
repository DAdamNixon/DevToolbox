using DevToolbox.Mcp.Core;
using DevToolbox.Services.Models;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The per-call location argument — the replacement for the locality half of
/// <see cref="LocationPolicy"/>, and now the only thing standing between a careless
/// <c>prepare_table</c> and an SMB walk across four production web servers.
/// <para>
/// These are unit tests of <see cref="LocationSelection.Resolve"/> itself. The service-level tests
/// in <c>LogViewerServiceTests</c> prove the argument is actually reached before any file is
/// touched; these prove it decides correctly, including the two cases that are easy to get wrong —
/// an unknown name, and a name that exists but is unusable.
/// </para>
/// </summary>
public sealed class LocationSelectionTests
{
    private static LogLocation At(string name, string path) => new() { Name = name, Path = path };

    private static readonly List<LogLocation> Configured =
    [
        At("Local Logs", @"C:\inetpub\LogFiles"),
        At("Live Web01", @"\\web01\inetpub\LogFiles"),
        At("Archived Logs", @"\\fileserver01\LogFiles\WebServers\ElliottLogs"),
        At("Broken", ""),
    ];

    [Fact]
    public void Named_locations_come_back_in_the_order_requested()
    {
        var selected = LocationSelection.Resolve(new[] { "Archived Logs", "Local Logs" }, Configured);

        Assert.Equal(new[] { "Archived Logs", "Local Logs" }, selected.Select(l => l.Name));
    }

    [Fact]
    public void A_network_location_can_be_selected()
    {
        // The whole reason the change was made. Before 2026-09-03 this was unreachable by any
        // argument at all.
        var selected = LocationSelection.Resolve(new[] { "Live Web01" }, Configured);

        Assert.Equal(@"\\web01\inetpub\LogFiles", Assert.Single(selected).Path);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        // The name is typed by a model reading a list. Case is not a meaningful distinction here, and
        // refusing on it would spend a whole round trip teaching nothing.
        var selected = LocationSelection.Resolve(new[] { "local logs" }, Configured);

        Assert.Equal("Local Logs", Assert.Single(selected).Name);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var selected = LocationSelection.Resolve(new[] { "  Local Logs  " }, Configured);

        Assert.Equal("Local Logs", Assert.Single(selected).Name);
    }

    [Fact]
    public void The_same_location_twice_is_collapsed_not_refused()
    {
        // Harmless, but it must not walk the directory twice and double the row count.
        var selected = LocationSelection.Resolve(new[] { "Local Logs", "LOCAL LOGS" }, Configured);

        Assert.Equal("Local Logs", Assert.Single(selected).Name);
    }

    [Fact]
    public void No_locations_is_refused_and_the_message_rules_out_a_default()
    {
        // Both defaults were considered and both are wrong: "all" is the accident this prevents,
        // "local" silently reinstates a guardrail that was deliberately retired. The message has to
        // say so, because an agent that sees only "required" will guess at what it should have sent.
        foreach (var empty in new IReadOnlyList<string>?[] { null, Array.Empty<string>() })
        {
            var ex = Assert.Throws<ArgumentException>(() => LocationSelection.Resolve(empty, Configured));
            Assert.Contains("no default", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("list_locations", ex.Message);
        }
    }

    [Fact]
    public void An_unknown_name_is_refused_and_lists_the_names_that_would_have_worked()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocationSelection.Resolve(new[] { "Live Web09" }, Configured));

        Assert.Contains("Live Web09", ex.Message);
        foreach (var known in Configured.Select(l => l.Name))
            Assert.Contains(known, ex.Message);
    }

    [Fact]
    public void One_unknown_name_refuses_the_whole_call_rather_than_reading_the_rest()
    {
        // THE test. Dropping the unknown name and reading the other two returns real rows, from a
        // real ingest, drawn from a smaller population than was asked for — no exception, no empty
        // result, nothing to notice. Same class of failure as a shared table handing one agent
        // another's rows, and as the UserTypes disagreement that cost the DB2 server a wrong answer.
        Assert.Throws<ArgumentException>(
            () => LocationSelection.Resolve(new[] { "Local Logs", "Live Web09", "Archived Logs" }, Configured));
    }

    [Fact]
    public void A_configured_but_unusable_name_is_refused_with_its_own_message()
    {
        // "You mistyped" and "your config is broken" send the dev to different places.
        var unusable = Assert.Throws<ArgumentException>(
            () => LocationSelection.Resolve(new[] { "Broken" }, Configured));
        var unknown = Assert.Throws<ArgumentException>(
            () => LocationSelection.Resolve(new[] { "Nope" }, Configured));

        Assert.Contains("Broken", unusable.Message);
        Assert.Contains(LocationPolicy.ReasonBlank, unusable.Message);
        Assert.DoesNotContain("Unknown location", unusable.Message);
        Assert.Contains("Unknown location", unknown.Message);
    }

    [Fact]
    public void A_blank_name_in_the_list_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocationSelection.Resolve(new[] { "Local Logs", "  " }, Configured));

        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void There_is_no_wildcard_that_selects_everything()
    {
        // Deliberately absent: a "*" token is one character away from the default that was rejected,
        // and it would be reached for exactly when the caller has not thought about cost. If this
        // test ever fails, someone added the shortcut back — that needs a decision, not a patch.
        foreach (var token in new[] { "*", "all", "ALL", "" })
        {
            Assert.Throws<ArgumentException>(() => LocationSelection.Resolve(new[] { token }, Configured));
        }
    }
}
