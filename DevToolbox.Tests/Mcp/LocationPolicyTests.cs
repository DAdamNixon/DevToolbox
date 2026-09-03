using DevToolbox.Mcp.Core;
using DevToolbox.Services.Models;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// What is left of the location policy after 2026-09-03: whether a configured location's path is a
/// path at all.
/// <para>
/// <b>This file used to assert the opposite of what it now asserts.</b> Until that date the policy
/// refused every UNC path and every network drive, and these tests were the whole enforcement of
/// "read only the local logs" — the comment here said that if it ever admitted a UNC path, an agent
/// would get a directory walk across a production web server and nothing else would notice.
/// </para>
/// <para>
/// That is still true, and it is still the hazard. What changed is where it is answered: locality
/// was scope control for a build phase, and the bound it was quietly also providing now lives in
/// <see cref="LocationSelection"/> as a required per-call argument. So the "a UNC path is refused"
/// tests were not deleted — they were inverted, and the ones that matter moved next door. Deleting
/// them would have left no record that the change was deliberate.
/// </para>
/// </summary>
public sealed class LocationPolicyTests
{
    private static LogLocation At(string path, string name = "test") => new() { Name = name, Path = path };

    [Fact]
    public void A_local_drive_path_is_usable()
    {
        Assert.Null(LocationPolicy.Refuse(At(@"C:\inetpub\LogFiles")));
        Assert.True(LocationPolicy.IsUsable(At(@"C:\inetpub\LogFiles")));
    }

    [Fact]
    public void A_unc_path_is_usable_now()
    {
        // The inverted test. The real config's nine network locations all take this shape, and every
        // one of them was refused here until the restriction was lifted.
        Assert.Null(LocationPolicy.Refuse(At(@"\\fileserver01\LogFiles\WebServers\ElliottLogs")));
        Assert.Null(LocationPolicy.Refuse(At(@"\\web01\inetpub\LogFiles")));
        Assert.Null(LocationPolicy.Refuse(At("//fileserver01/LogFiles")));
    }

    [Fact]
    public void A_relative_path_is_still_refused()
    {
        // Locality stopped mattering; being a path did not. A relative path resolves against whatever
        // the working directory happens to be, which is not a location anyone configured.
        Assert.Equal(LocationPolicy.ReasonNotRooted, LocationPolicy.Refuse(At(@"LogFiles\Web")));
        Assert.Equal(LocationPolicy.ReasonNotRooted, LocationPolicy.Refuse(At(@"..\..\LogFiles")));
    }

    [Fact]
    public void A_blank_path_is_refused_and_says_so_distinctly()
    {
        // A separate reason on purpose: a location with no path is a broken config entry, and telling
        // the dev that apart from a decision is now the policy's entire job.
        Assert.Equal(LocationPolicy.ReasonBlank, LocationPolicy.Refuse(At("")));
        Assert.Equal(LocationPolicy.ReasonBlank, LocationPolicy.Refuse(At("   ")));
    }

    [Fact]
    public void Existence_is_not_part_of_the_decision()
    {
        // Truer now than when everything usable was local. A share is unreachable because the server
        // is down, the VPN is off, or the account has no rights today — all facts about this moment,
        // none about log_paths.yaml. Refusing would send the dev to edit a correct file.
        Assert.Null(LocationPolicy.Refuse(At(@"C:\this\does\not\exist\anywhere")));
        Assert.Null(LocationPolicy.Refuse(At(@"\\nosuchserver\nosuchshare")));
    }

    [Fact]
    public void Every_refusal_reason_says_what_is_wrong_with_the_configuration()
    {
        // A refusal a dev cannot act on is only slightly better than a silent drop.
        foreach (var reason in new[] { LocationPolicy.ReasonBlank, LocationPolicy.ReasonNotRooted })
        {
            Assert.StartsWith("Refused:", reason);
            Assert.Contains("path", reason, StringComparison.OrdinalIgnoreCase);
        }
    }
}
