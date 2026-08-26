using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// What the dashboard search box counts as a hit. Three ways in — substring, alias,
/// abbreviation — and the rule that matters is that adding the last two never removes a
/// result the first one would have found.
/// </summary>
public class WorkspaceSearchTests
{
    [Theory]
    [InlineData("account", "AccountInquiry")]      // substring, any case
    [InlineData("Inquiry", "AccountInquiry")]      // substring from the middle
    [InlineData("accinq", "AccountInquiry")]       // abbreviation
    public void Name_matches_by_substring_or_abbreviation(string query, string name)
    {
        Assert.True(WorkspaceSearch.MatchesName(query, name));
    }

    [Fact]
    public void An_alias_matches_when_the_name_cannot()
    {
        // Nothing about "billing" is in "InvoiceApproval". The alias is the only route.
        Assert.False(WorkspaceSearch.MatchesName("billing", "InvoiceApproval"));
        Assert.True(WorkspaceSearch.MatchesName("billing", "InvoiceApproval", new[] { "invapp", "billing" }));
    }

    [Fact]
    public void A_prefix_of_an_alias_is_enough()
    {
        // An alias is a word the user chose, so they should not have to finish typing it.
        Assert.True(WorkspaceSearch.MatchesName("bill", "InvoiceApproval", new[] { "billing" }));
    }

    [Fact]
    public void Blank_and_empty_aliases_are_ignored()
    {
        // A hand-edited YAML list can hold a stray "- " and an empty alias must not match
        // everything typed into the box.
        Assert.False(WorkspaceSearch.MatchesName("zzz", "Account", new[] { "", "   " }));
    }

    [Fact]
    public void An_empty_query_matches_everything()
    {
        // The search box empties one keystroke at a time; the last one must not hide the page.
        Assert.True(WorkspaceSearch.MatchesName("", "Account"));
        Assert.True(WorkspaceSearch.MatchesName("   ", "Account"));
        Assert.True(WorkspaceSearch.MatchesPath("", @"C:\tfs\thing.sln"));
    }

    [Fact]
    public void Paths_match_on_substring_only()
    {
        const string path = @"C:\tfs\elliottelectric_com\demo\wwwroot\Account\Account.Demo.slnf";

        Assert.True(WorkspaceSearch.MatchesPath("wwwroot", path));
        Assert.True(WorkspaceSearch.MatchesPath("Account.Demo", path));

        // "cdw" is a fine abbreviation of "...com\demo\wwwroot..." and matching it would mean
        // every card whose path shares a middle with its siblings — which is all of them.
        Assert.False(WorkspaceSearch.MatchesPath("cdw", path));
    }

    // ---- short queries must start a word ----------------------------------------------------

    [Theory]
    // The reported case: "ai" matched 47 of 457 cards, almost all of them through the middle of
    // an ordinary English word. Two and three letters are too common to be allowed in there.
    [InlineData("ai", "Email.Protocol.POP3")]
    [InlineData("ai", "EES.Waivers")]
    [InlineData("ai", "EES.Chaining.Standard")]
    [InlineData("ai", "Maint")]
    [InlineData("ai", "WebDevTraining")]
    [InlineData("ain", "Training")]
    public void A_short_query_does_not_match_the_middle_of_a_word(string query, string name)
    {
        Assert.False(WorkspaceSearch.MatchesName(query, name), $"'{query}' should not match '{name}'");
    }

    [Theory]
    // What "ai" is actually for, all of it at a word start or as initials.
    [InlineData("ai", "AIM Services")]
    [InlineData("ai", "EES.Aim")]              // after the dot
    [InlineData("ai", "AIMBackgroundProcesses")]
    [InlineData("ai", "Bubo AI File Generator")]
    [InlineData("ai", "AccountInquiry")]       // initials, both on humps
    [InlineData("ai", "Age Inventory")]
    public void A_short_query_still_matches_at_a_word_start(string query, string name)
    {
        Assert.True(WorkspaceSearch.MatchesName(query, name), $"'{query}' should match '{name}'");
    }

    [Theory]
    // At four characters a query is specific enough that a mid-word hit is meant.
    [InlineData("count", "EES.Account")]
    [InlineData("mail", "MarketingEmailerEOD")]
    [InlineData("nquiry", "AccountInquiry")]
    public void A_longer_query_may_match_the_middle_of_a_word(string query, string name)
    {
        Assert.True(WorkspaceSearch.MatchesName(query, name), $"'{query}' should match '{name}'");
    }

    // ---- the \Main\ problem ------------------------------------------------------------------

    [Fact]
    public void A_substring_inside_a_word_is_not_a_path_hit()
    {
        // The bug this rule exists for. All 47 NuGet packages live under a \Main\ branch folder,
        // so "ai" — sitting inside "M-ai-n" — matched every one of them.
        const string path = @"C:\tfs\common\NuGetPackages\EES.Account\Main\EES.Account.sln";

        Assert.False(WorkspaceSearch.MatchesPath("ai", path));
        Assert.False(WorkspaceSearch.MatchesPath("ackage", path));
        Assert.False(WorkspaceSearch.MatchesPath("ommon", path));
    }

    [Theory]
    // The things people actually search a path for, all of which start a word.
    [InlineData("main")]                    // after a separator
    [InlineData("Main")]
    [InlineData("NuGetPackages")]
    [InlineData("Get")]                     // a camel hump inside NuGetPackages
    [InlineData("EES.Account")]
    [InlineData("sln")]                     // after the dot
    [InlineData(@"tfs\common")]             // spanning a separator
    [InlineData(@"C:\tfs")]                 // a pasted absolute path, from index 0
    public void A_substring_that_starts_a_word_is_a_path_hit(string query)
    {
        const string path = @"C:\tfs\common\NuGetPackages\EES.Account\Main\EES.Account.sln";

        Assert.True(WorkspaceSearch.MatchesPath(query, path), $"'{query}' should match the path");
    }

    [Fact]
    public void One_boundary_hit_anywhere_in_the_path_is_enough()
    {
        // "main" appears twice: mid-word in "Domain" first, then starting a word in "\Main\".
        // Finding the mid-word one first must not settle the question.
        Assert.True(WorkspaceSearch.MatchesPath("main", @"C:\tfs\Domain\Main\thing.sln"));

        // And with only the mid-word occurrence, it stays a miss.
        Assert.False(WorkspaceSearch.MatchesPath("main", @"C:\tfs\Domain\thing.sln"));
    }

    [Fact]
    public void A_short_query_still_reaches_a_folder_that_really_starts_with_it()
    {
        // The rule narrows what "ai" matches; it does not stop it matching an actual Aim folder.
        Assert.True(WorkspaceSearch.MatchesPath("ai", @"C:\tfs\common\NuGetPackages\EES.Aim\Main\EES.Aim.sln"));
    }

    [Fact]
    public void Whitespace_around_a_query_is_trimmed()
    {
        // Paths get pasted in, and a pasted path usually arrives with something on the end.
        Assert.True(WorkspaceSearch.MatchesName("  account  ", "AccountInquiry"));
        Assert.True(WorkspaceSearch.MatchesPath(" wwwroot ", @"C:\tfs\wwwroot\x.sln"));
    }

    [Fact]
    public void Null_name_and_null_path_are_not_matches()
    {
        Assert.False(WorkspaceSearch.MatchesName("a", null));
        Assert.False(WorkspaceSearch.MatchesPath("a", null));
    }
}
