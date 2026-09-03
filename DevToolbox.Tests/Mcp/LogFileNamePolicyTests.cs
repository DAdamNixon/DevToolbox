using DevToolbox.Mcp.Core;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The log-file-name policy — the argument-side half of the readable-set guarantee, and the half
/// that was missing until 2026-09-03.
/// <para>
/// These are not shape-checking tests dressed up as security ones. Before the policy existed,
/// <c>..\..\Windows\Temp\x</c> was accepted by <c>prepare_table</c> and issued a handle: the value
/// is interpolated into a search pattern, and a .NET search pattern may contain directory
/// separators, so the search ran outside every admitted location. It returned no rows only because
/// nothing there matched the prefix — which is not a refusal, and is exactly the kind of result
/// that reads as safe while proving nothing.
/// <see cref="Traversal_out_of_the_location_is_refused_and_the_escape_is_real"/> is written to
/// keep that distinction honest by demonstrating both halves.
/// </para>
/// </summary>
public sealed class LogFileNamePolicyTests
{
    [Fact]
    public void A_plain_log_name_is_accepted()
    {
        // The shapes list_log_files actually reports, dots and all — the policy must not narrow
        // the names the server already serves.
        Assert.Null(LogFileNamePolicy.Refuse("Checkout"));
        Assert.Null(LogFileNamePolicy.Refuse("Checkout.WithAccount.Modern"));
        Assert.Null(LogFileNamePolicy.Refuse("P.Microsoft.SemanticKernel.Connectors.AzureOpenAI"));
        Assert.True(LogFileNamePolicy.IsAcceptable("OverpricedCache"));
    }

    [Theory]
    [InlineData(@"..\..\Windows\Temp\x")]
    [InlineData("../../Windows/Temp/x")]
    [InlineData(@"..\decoy")]
    [InlineData(@"sub\Checkout")]
    [InlineData("sub/Checkout")]
    public void A_name_carrying_a_directory_separator_is_refused(string name)
    {
        // Both separators, because Windows resolves them identically and checking only the
        // backslash would leave a spelling of the same traversal that walks past the policy —
        // the same trap LocalPathPolicy documents for //server/share.
        Assert.Equal(LogFileNamePolicy.ReasonPathShape, LogFileNamePolicy.Refuse(name));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\fileserver01\programmer01\evil")]
    [InlineData("C:hosts")]
    public void A_rooted_or_unc_name_is_refused_by_policy_rather_than_by_accident(string name)
    {
        // These were already refused before the policy, but by a Path.Combine deep in the BCL
        // throwing "Second path fragment must not be a drive or UNC name. (Parameter 'expression')"
        // — a foreign message handed to the caller verbatim. The outcome was right and the reason
        // was luck. Asserting our own text is what pins it down; "C:hosts" is included because it
        // is drive-relative with no separator at all.
        Assert.Equal(LogFileNamePolicy.ReasonPathShape, LogFileNamePolicy.Refuse(name));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Check*out")]
    [InlineData("Checkout?")]
    public void A_wildcard_is_refused(string name)
    {
        // The server appends its own '*' to make this a prefix match. A caller-supplied wildcard
        // widens that match without appearing anywhere in what prepare_table reports back, so the
        // rows arrive from a population the caller never named.
        Assert.Equal(LogFileNamePolicy.ReasonPathShape, LogFileNamePolicy.Refuse(name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void A_name_of_only_dots_is_refused(string name)
    {
        // No separator, so the invalid-character check does not see these; "." was accepted and
        // issued a handle before this policy existed.
        Assert.Equal(LogFileNamePolicy.ReasonPathShape, LogFileNamePolicy.Refuse(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused_and_says_so_distinctly(string? name)
    {
        // A separate reason from the rest on purpose: an empty argument is a caller who has not
        // called list_log_files yet, not a caller attempting a path. Telling them apart is the
        // point of reporting refusals at all — the same distinction LocalPathPolicy draws
        // between ReasonBlank and the others.
        Assert.Equal(LogFileNamePolicy.ReasonBlank, LogFileNamePolicy.Refuse(name));
    }

    [Fact]
    public async Task Traversal_out_of_the_location_is_refused_and_the_escape_is_real()
    {
        // The test that gives the rest their meaning. It asserts two things, and the first is
        // what stops the second from being vacuous:
        //
        //   1. The platform really does honour "..\" inside a search pattern, so a file one level
        //      above the admitted location IS reachable by the exact call prepare_table makes.
        //   2. prepare_table refuses to make that call.
        //
        // Without (1) this would pass just as happily on a platform where traversal was never
        // possible, and would report a guardrail that nothing was testing.
        using var env = new LogEnvironment();

        var outside = Path.GetDirectoryName(env.LogFolder)!;
        var decoyName = $"decoy{Guid.NewGuid():N}";
        var decoyPath = Path.Combine(outside, $"{decoyName}.20260821.txt");
        File.WriteAllLines(decoyPath, [LogEnvironment.Line("20260821120000", "1", "ESCAPED", "secret")]);

        try
        {
            // (1) The escape is real: this is DbLogService's call, with the traversal in place.
            var reachable = new DirectoryInfo(env.LogFolder)
                .EnumerateFiles($@"..\{decoyName}*.txt")
                .ToList();
            Assert.Single(reachable);

            // (2) And prepare_table will not make it.
            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => env.Service.PrepareAsync($@"..\{decoyName}", "Checkout", "2026-08-21", "2026-08-21", LogEnvironment.LocalOnly));
            Assert.Contains(LogFileNamePolicy.ReasonPathShape, refused.Message);
        }
        finally
        {
            File.Delete(decoyPath);
        }
    }

    [Fact]
    public async Task A_well_formed_name_that_matches_nothing_is_an_empty_result_not_a_refusal()
    {
        // The distinction the policy is written not to collapse. A typo has to look like a typo:
        // zero rows and a handle, not a refusal that reads as "you may not ask this".
        using var env = new LogEnvironment();

        var prepared = await env.Service.PrepareAsync("NoSuchLogEverExisted", "Checkout", "2026-08-21", "2026-08-21", LogEnvironment.LocalOnly);

        Assert.Equal(0, prepared.Rows);
        Assert.False(string.IsNullOrWhiteSpace(prepared.Handle));
    }
}
