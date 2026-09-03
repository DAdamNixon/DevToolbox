using DevToolbox.Mcp.Core;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The name-collision check — the design-time half of the prefix-match problem.
/// <para>
/// <c>prepare_table</c> matches <c>logFile</c> on a prefix and its response never names the files
/// it actually read, so two flows can share one handle and a bare <c>COUNT(*)</c> sums both. On
/// 2026-09-02 that returned 93 Checkout order completes when the truth was 75. Mandatory
/// <c>GROUP BY [SourceFile]</c> makes the mistake forbidden; a name that cannot collide makes it
/// unavailable. This type is how a designer finds out which one they are choosing.
/// </para>
/// <para>
/// <see cref="The_real_WithAccount_collision_that_cost_a_wrong_answer"/> is the regression test
/// that matters: it reproduces the exact pair of names from that day rather than an invented one,
/// so the test fails if the direction reasoning is ever reversed.
/// </para>
/// </summary>
public sealed class LogNameCollisionTests
{
    private static DiscoveredName[] CheckoutFamily() =>
    [
        new("Checkout", 2687),
        new("Checkout.Pickup", 1240),
        new("Checkout.CashSale", 980),
        new("Checkout.WithAccount", 892),
        new("Checkout.WithAccount.Modern", 2004),
        new("Checkout.Shipping", 1500),
        new("OverpricedCache", 3119),
    ];

    [Fact]
    public void The_real_WithAccount_collision_that_cost_a_wrong_answer()
    {
        // Proposing the name that already exists alongside a longer sibling. Querying
        // "Checkout.WithAccount" pulls ".Modern" too — the 93-vs-75 error.
        var hits = LogNameCollision.Find("Checkout.WithAccount", CheckoutFamily());

        var modern = Assert.Single(hits, c => c.ExistingName == "Checkout.WithAccount.Modern");
        Assert.Equal(LogNameCollision.ProposedIsPrefix, modern.Direction);
        Assert.True(modern.AtSeparator);

        // The exact match is reported too, and not silently folded into "clean".
        Assert.Contains(hits, c => c.Direction == LogNameCollision.ExactMatch);
    }

    [Fact]
    public void Creating_a_longer_name_reports_the_worse_direction_first()
    {
        // The damaging direction: adding "Checkout.WithAccount.Modern" widens every query already
        // written against "Checkout.WithAccount" and against "Checkout". Those queries work today
        // and silently stop being correct — strictly worse than misleading a query nobody wrote.
        var hits = LogNameCollision.Find("Checkout.WithAccount.Modern", CheckoutFamily());

        Assert.Equal(LogNameCollision.ExistingIsPrefix, hits[0].Direction);
        Assert.All(
            hits.Where(h => h.ExistingName is "Checkout" or "Checkout.WithAccount"),
            h => Assert.Equal(LogNameCollision.ExistingIsPrefix, h.Direction));

        // Ordering within a direction is by how much history is at stake.
        var existingPrefixes = hits.Where(h => h.Direction == LogNameCollision.ExistingIsPrefix).ToList();
        Assert.True(existingPrefixes[0].FileCount >= existingPrefixes[^1].FileCount);
    }

    [Fact]
    public void The_plain_Checkout_name_collides_with_its_whole_family()
    {
        // Why "Checkout" is a trap that cannot be renamed: it is the legacy mobile page AND a
        // prefix of every Checkout.* file. Anything proposed as a bare app name inherits this.
        var hits = LogNameCollision.Find("Checkout", CheckoutFamily());

        Assert.Equal(6, hits.Count);                            // five siblings + the exact match
        Assert.DoesNotContain(hits, c => c.ExistingName == "OverpricedCache");
    }

    [Fact]
    public void A_mid_token_overlap_is_reported_and_flagged_as_not_at_a_separator()
    {
        // The one people miss. "CheckoutLegacy" does not read as a relative of "Checkout", but the
        // search pattern is a raw string prefix and knows nothing about dots, so querying
        // "Checkout" ingests it. Reported distinctly rather than folded in.
        var hits = LogNameCollision.Find("CheckoutLegacy", CheckoutFamily());

        var hit = Assert.Single(hits);
        Assert.Equal("Checkout", hit.ExistingName);
        Assert.Equal(LogNameCollision.ExistingIsPrefix, hit.Direction);
        Assert.False(hit.AtSeparator);
    }

    [Fact]
    public void A_genuinely_new_name_is_clean()
    {
        Assert.Empty(LogNameCollision.Find("Warehouse.Receiving", CheckoutFamily()));

        // And the fix the advisor recommends actually works: branching both sides removes the
        // overlap that extending the name creates.
        var branched = new DiscoveredName[] { new("Checkout.WithAccount.Classic", 892), new("Checkout.WithAccount.Modern", 2004) };
        Assert.Empty(LogNameCollision.Find("Checkout.WithAccount.Mobile", branched));
    }

    [Fact]
    public void Matching_ignores_case_because_these_are_Windows_filenames()
    {
        var hits = LogNameCollision.Find("checkout.withaccount", CheckoutFamily());

        Assert.Contains(hits, c => c.ExistingName == "Checkout.WithAccount" && c.Direction == LogNameCollision.ExactMatch);
        Assert.Contains(hits, c => c.ExistingName == "Checkout.WithAccount.Modern");
    }

    [Fact]
    public void Blank_and_null_input_return_no_collisions_rather_than_throwing()
    {
        // Validating the name itself belongs to LogFileNamePolicy. Duplicating that rule here
        // would put one decision in two places, and they would drift.
        Assert.Empty(LogNameCollision.Find(null, CheckoutFamily()));
        Assert.Empty(LogNameCollision.Find("   ", CheckoutFamily()));
        Assert.Empty(LogNameCollision.Find("Checkout", null));
        Assert.Empty(LogNameCollision.Find("Checkout", []));
    }

    [Fact]
    public void A_blank_existing_name_is_skipped_not_matched()
    {
        // Every string starts with "", so an empty discovered name would otherwise collide with
        // everything and bury the real answer.
        var hits = LogNameCollision.Find("Checkout", [new("", 3), new("   ", 1), new("Checkout.Pickup", 10)]);

        var hit = Assert.Single(hits);
        Assert.Equal("Checkout.Pickup", hit.ExistingName);
    }
}
