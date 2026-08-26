using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The dashboard search box accepts abbreviations, and the whole question is where the line
/// sits: loose enough that <c>persman</c> reaches <c>PersonnelManagement</c>, tight enough
/// that three letters do not light up a screen of 460 cards.
/// <para>
/// The line drawn is "every run of matched characters starts at a word boundary". These tests
/// are the two halves of that: real abbreviations that must land, and plausible-looking
/// queries that must not.
/// </para>
/// </summary>
public class FuzzyMatchTests
{
    // ---- what has to match ------------------------------------------------------------------

    [Theory]
    [InlineData("persman", "PersonnelManagement")]   // the case this was built for
    [InlineData("persman", "personnelmanagement")]   // and the same name with no humps to land on
    [InlineData("PersMan", "personnelmanagement")]   // case is irrelevant in both directions
    [InlineData("pers", "PersonnelManagement")]      // a plain prefix
    [InlineData("management", "PersonnelManagement")] // a whole later word
    [InlineData("eesws", "EESWebShares")]            // acronym, then the hump inside it
    [InlineData("eeswebshares", "EESWebShares")]     // the whole name
    [InlineData("accinq", "AccountInquiry")]
    [InlineData("ai", "AccountInquiry")]             // initials
    [InlineData("app1", "AspireApp1")]               // a digit starts a boundary
    [InlineData("aimserv", "AIM Services")]          // a space is a boundary
    [InlineData("aim serv", "AIM Services")]         // and a space typed in the query is ignored
    [InlineData("eescheckout", "EES.CheckoutOrder")] // a dot is a boundary too
    [InlineData("ees.checkout", "EES.CheckoutOrder")]
    [InlineData("mobwebshares", "EESMobileWebShares")]
    public void Matches_abbreviations(string query, string candidate)
    {
        Assert.True(FuzzyMatch.IsMatch(query, candidate), $"'{query}' should match '{candidate}'");
    }

    // ---- what must not ---------------------------------------------------------------------

    [Theory]
    // The 'c' would have to be one stray character from inside "Backend". A mid-word run has
    // to be worth at least two characters, which is what stops a short query reaching anywhere.
    // (Two adjacent characters mid-word *would* be allowed — "abc" does match "aaabbbccc"
    // through its "bc" — but that is a made-up string, not a project name.)
    [InlineData("abc", "AccountInquiryBackend")]
    // Right letters, wrong order.
    [InlineData("manpers", "PersonnelManagement")]
    // A letter the name does not contain at all.
    [InlineData("persmanz", "PersonnelManagement")]
    // Starting mid-word is not an abbreviation of anything.
    [InlineData("ersonnel", "PersonnelManagement")]
    // Longer than the name it is supposed to abbreviate.
    [InlineData("personnelmanagementsystem", "PersonnelManagement")]
    public void Rejects_non_abbreviations(string query, string candidate)
    {
        Assert.False(FuzzyMatch.IsMatch(query, candidate), $"'{query}' should not match '{candidate}'");
    }

    [Theory]
    [InlineData("", "Account")]
    [InlineData("   ", "Account")]
    [InlineData(null, "Account")]
    [InlineData("a", "")]
    [InlineData("a", null)]
    public void Blank_input_is_not_a_match(string? query, string? candidate)
    {
        // Not a match, and specifically not a crash: the search box asks this of every card on
        // every keystroke, including the keystroke that empties it.
        Assert.False(FuzzyMatch.IsMatch(query, candidate));
        Assert.Equal(0, FuzzyMatch.Score(query, candidate));
    }

    // ---- scoring ---------------------------------------------------------------------------

    [Fact]
    public void Prefix_beats_a_split_match()
    {
        // Both match; the one that reads as an abbreviation of the front of the name is better.
        var prefix = FuzzyMatch.Score("acc", "AccountInquiry");
        var split = FuzzyMatch.Score("ai", "AccountInquiry");

        Assert.True(prefix > split, $"prefix {prefix} should beat initials {split}");
    }

    [Fact]
    public void Fewer_runs_beat_more()
    {
        // "EESWeb" is one unbroken run; "eesws" needs a second run for its final s.
        var whole = FuzzyMatch.Score("eesweb", "EESWebShares");
        var split = FuzzyMatch.Score("eesws", "EESWebShares");

        Assert.True(whole > split, $"one run {whole} should beat two {split}");
    }

    [Fact]
    public void A_long_candidate_is_left_alone()
    {
        // Paths are matched as substrings elsewhere, and are the reason for the length cap:
        // fuzzy-matching one produces a hit for almost any query, which is worse than no hit.
        var path = new string('a', 200);

        Assert.Equal(0, FuzzyMatch.Score("aaa", path));
    }
}
