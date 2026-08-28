using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DevToolbox.Services.Models;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// A theme is spread over four files by necessity, and this is what stops them drifting apart.
/// <para>
/// The palette has to be on <c>&lt;html&gt;</c> before the first paint, which happens long before
/// any .NET code has read a config file — so the boot script carries its own copy of the theme list
/// in <c>wwwroot/js/themeCatalog.js</c>, and <see cref="ThemeCatalog"/> carries the copy the
/// Settings page reads. Neither can be derived from the other at runtime. What can be checked is
/// that they agree, and that the CSS each one names actually exists, which is what these tests do.
/// </para>
/// <para>
/// Adding a theme should fail here first, with a message naming the file that was forgotten.
/// </para>
/// </summary>
public class ThemeCatalogTests
{
    /// <summary>
    /// The wwwroot the tests read, found by walking up to the solution file. The CSS and JS here are
    /// static content, not compiled or copied, so there is nothing in the output directory to look
    /// at — the source tree is the only copy there is.
    /// </summary>
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "DevToolbox.UI", "wwwroot");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DevToolbox.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadWeb(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { WebRoot }.Concat(parts).ToArray()));

    // ---- the JS catalog, as data ------------------------------------------------------------

    private sealed record JsTheme(string Id, string? Season, string? Effect, bool Auto);

    /// <summary>
    /// Parses <c>themeCatalog.js</c> with a regex rather than a JS engine. That is why the file is
    /// documented as one theme per line in a fixed field order: the format is a contract with this
    /// method, and a reformat that breaks it should break here rather than at runtime.
    /// <para>
    /// Ids are lowercase with hyphens allowed — <c>better-christmas</c> is the first of those, and
    /// before it the pattern accepted <c>[a-z]+</c> only, which would have skipped the row silently
    /// and reported it as missing from the JS catalog rather than as an unparseable one.
    /// </para>
    /// </summary>
    private static List<JsTheme> ParseJsCatalog()
    {
        var source = ReadWeb("js", "themeCatalog.js");

        var entries = Regex.Matches(
            source,
            @"\{\s*id:\s*'(?<id>[a-z][a-z-]*)',\s*season:\s*(?:null|'(?<season>[^']*)'),\s*effect:\s*(?:null|'(?<effect>[^']*)'),\s*auto:\s*(?<auto>true|false)\s*\}");

        Assert.NotEmpty(entries);

        return entries.Select(m => new JsTheme(
            m.Groups["id"].Value,
            m.Groups["season"].Success ? m.Groups["season"].Value : null,
            m.Groups["effect"].Success ? m.Groups["effect"].Value : null,
            m.Groups["auto"].Value == "true")).ToList();
    }

    [Fact]
    public void The_javascript_catalog_lists_the_same_themes_in_the_same_order()
    {
        var fromJs = ParseJsCatalog();
        var fromCs = ThemeCatalog.All
            .Select(t => new JsTheme(t.Id, t.Season?.Wire, t.Effect, t.Automatic))
            .ToList();

        // Order matters as well as content: the dropdown is built from the C# list and the paint
        // decision from the JS one, so a theme that exists in only one of them is a theme that can
        // be selected and never appear, or appear and never be selectable.
        Assert.Equal(fromCs, fromJs);
    }

    [Fact]
    public void Every_theme_that_names_an_effect_has_one_that_is_tuned_and_drawn()
    {
        var catalogJs = ReadWeb("js", "themeCatalog.js");
        var effectsCss = ReadWeb("css", "themeEffects.css");

        foreach (var effect in ThemeCatalog.All.Select(t => t.Effect).Where(e => e is not null).Distinct())
        {
            // The particle count and size range, without which themeEffects.js builds nothing.
            Assert.True(
                Regex.IsMatch(catalogJs, $@"\b{Regex.Escape(effect!)}:\s*\{{\s*count:\s*\d+"),
                $"themeCatalog.js has no effects entry for '{effect}', so it would animate nothing.");

            // The glyph and motion, without which it builds invisible particles.
            Assert.Contains($"[data-fx=\"{effect}\"]", effectsCss);
        }
    }

    // ---- the CSS -----------------------------------------------------------------------------

    /// <summary>
    /// The token contract, read off the base <c>:root</c> block in theme.css rather than written out
    /// here. Adding a token to the base palette therefore makes it required of every seasonal one,
    /// with no second list to remember to update.
    /// </summary>
    private static List<string> RequiredTokens()
    {
        var themeCss = ReadWeb("css", "theme.css");

        // The bare `:root {` block, not `:root[data-theme="light"]` — the light theme is an override
        // and is allowed to be a partial one.
        var baseBlock = Regex.Match(themeCss, @":root\s*\{(?<body>[^}]*)\}");
        Assert.True(baseBlock.Success, "theme.css has no base :root block to read the contract from.");

        var tokens = Regex.Matches(baseBlock.Groups["body"].Value, @"(--[a-z0-9-]+)\s*:")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(tokens);
        return tokens;
    }

    [Fact]
    public void Every_seasonal_theme_has_a_palette_file_that_is_imported_and_complete()
    {
        var manifest = ReadWeb("css", "themes.css");
        var required = RequiredTokens();

        foreach (var theme in ThemeCatalog.All.Where(t => t.IsSeasonal))
        {
            var path = Path.Combine(WebRoot, "css", "themes", $"{theme.Id}.css");
            Assert.True(File.Exists(path), $"No palette at css/themes/{theme.Id}.css for theme '{theme.Id}'.");

            // Listed, or the file is never loaded and the theme paints as plain dark.
            Assert.Contains($"@import url(\"themes/{theme.Id}.css\")", manifest);

            var palette = File.ReadAllText(path);

            // The selector has to match what theme.js writes onto <html>.
            Assert.Contains($":root[data-theme=\"{theme.Id}\"]", palette);

            // color-scheme drives the native scrollbars and form controls; a palette that leaves it
            // out gets the dark ones, which on Winter's near-white ground is the bug this catches.
            Assert.Contains("color-scheme:", palette);

            // Every token, not just the ones that differ. An omitted token falls back to the base
            // :root value, which for a light-grounded theme means white text on white.
            var missing = required.Where(token => !Regex.IsMatch(palette, $@"{Regex.Escape(token)}\s*:")).ToList();
            Assert.True(
                missing.Count == 0,
                $"css/themes/{theme.Id}.css does not declare: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void A_standard_theme_has_no_palette_file_of_its_own()
    {
        // dark and light live in theme.css; a stray themes/dark.css would load after it and win
        // silently, which is a confusing place to lose an edit.
        foreach (var theme in ThemeCatalog.All.Where(t => !t.IsSeasonal))
        {
            Assert.False(
                File.Exists(Path.Combine(WebRoot, "css", "themes", $"{theme.Id}.css")),
                $"'{theme.Id}' is a standard theme but has a css/themes/{theme.Id}.css that would override theme.css.");
        }
    }

    // ---- the season rule ---------------------------------------------------------------------

    [Theory]
    [InlineData(9, 1, true)]     // first day
    [InlineData(10, 15, true)]
    [InlineData(11, 30, true)]   // last day, inclusive
    [InlineData(8, 31, false)]
    [InlineData(12, 1, false)]
    public void A_season_includes_both_of_its_end_days(int month, int day, bool expected)
    {
        var fall = new ThemeSeason(9, 1, 11, 30);
        Assert.Equal(expected, fall.Contains(new DateOnly(2026, month, day)));
    }

    [Theory]
    [InlineData(12, 1, true)]
    [InlineData(12, 31, true)]
    [InlineData(1, 1, true)]     // the far side of the wrap
    [InlineData(2, 28, true)]
    [InlineData(3, 1, false)]
    [InlineData(11, 30, false)]
    public void A_season_may_wrap_the_new_year(int month, int day, bool expected)
    {
        // Winter's window ends before it starts, which is what makes it a union of two ranges
        // rather than the empty intersection a naive comparison would give.
        var winter = new ThemeSeason(12, 1, 2, 29);
        Assert.Equal(expected, winter.Contains(new DateOnly(2026, month, day)));
    }

    [Fact]
    public void February_29th_in_a_window_still_includes_the_28th_of_a_common_year()
    {
        // The stored end day need not be a real date in the year being asked about; only the month
        // and day are compared, which is the whole reason there is no year in ThemeSeason.
        Assert.True(new ThemeSeason(12, 1, 2, 29).Contains(new DateOnly(2027, 2, 28)));
    }

    // ---- what is offered ---------------------------------------------------------------------

    [Fact]
    public void Out_of_season_only_the_always_available_themes_are_offered()
    {
        // Late May, which is the gap the calendar still has: Easter's window closes on 25 April
        // and Fourth of July's opens on 1 July. This test used to use the 4th of July itself,
        // which stopped being out of season the day that theme was added — a good failure, and
        // the reason the date is now chosen against the catalog rather than for being memorable.
        var gap = new DateOnly(2026, 5, 20);

        // Seasonal belongs here rather than in the seasonal group: it is the rule that follows the
        // calendar, so the calendar must never be able to remove it.
        Assert.Equal(
            new[] { ThemeOptions.Seasonal, ThemeOptions.System, ThemeOptions.Dark, ThemeOptions.Light },
            ThemeCatalog.Offered(gap, showAll: false).Select(t => t.Id));
    }

    [Fact]
    public void In_October_fall_and_halloween_are_both_offered()
    {
        var ids = ThemeCatalog.Offered(new DateOnly(2026, 10, 20), showAll: false).Select(t => t.Id).ToList();

        // The windows overlap on purpose — the two are meant to be a choice during October, not a
        // sequence.
        Assert.Contains("fall", ids);
        Assert.Contains("halloween", ids);
        Assert.DoesNotContain("christmas", ids);
    }

    [Fact]
    public void Show_all_themes_ignores_the_calendar_entirely()
    {
        Assert.Equal(
            ThemeCatalog.All.Select(t => t.Id),
            ThemeCatalog.Offered(new DateOnly(2026, 7, 4), showAll: true).Select(t => t.Id));
    }

    [Fact]
    public void A_seasonal_choice_is_kept_but_dormant_once_its_season_passes()
    {
        // The point of the whole design: the setting survives so the theme comes back next year,
        // and IsActive is how the UI knows to explain why the window is not green.
        Assert.True(ThemeCatalog.IsActive("christmas", new DateOnly(2026, 12, 25), showAll: false));
        Assert.False(ThemeCatalog.IsActive("christmas", new DateOnly(2027, 1, 2), showAll: false));
        Assert.True(ThemeCatalog.IsActive("christmas", new DateOnly(2027, 1, 2), showAll: true));
    }

    // ---- the saved setting -------------------------------------------------------------------

    [Fact]
    public void A_seasonal_theme_survives_normalization_out_of_season()
    {
        // UiSettingsService normalizes on every load and save. If that dropped an out-of-season id
        // the setting would erase itself in January, which is exactly what must not happen.
        Assert.Equal("christmas", ThemeOptions.Normalize("christmas"));
        Assert.Equal("halloween", ThemeOptions.Normalize("HALLOWEEN"));
    }

    [Fact]
    public void An_unknown_theme_falls_back_to_following_the_system()
    {
        // Was "easter", until Easter became a theme. Any string that is not an id will do; this one
        // is deliberately not a holiday, so it does not become a theme either.
        Assert.Equal(ThemeOptions.System, ThemeOptions.Normalize("chartreuse"));
        Assert.Equal(ThemeOptions.System, ThemeOptions.Normalize(null));
        Assert.Equal(ThemeOptions.System, ThemeOptions.Normalize(""));
    }

    [Fact]
    public void The_defaults_are_the_system_theme_animated_and_no_out_of_season_themes()
    {
        var defaults = new UiSettings();

        Assert.Equal(ThemeOptions.System, defaults.Theme);
        Assert.True(defaults.ThemeAnimations);
        Assert.False(defaults.ShowAllThemes);
    }

    // ---- the seasonal rule -------------------------------------------------------------------
    //
    // The "seasonal" theme is the only setting in the app that repaints without being asked, so
    // these are the tests for the promise the Settings page makes: that the seasons arrive on their
    // own, and that where two windows cover the same day there is exactly one answer.

    [Theory]
    [InlineData(9, 15, "fall")]
    [InlineData(10, 1, "halloween")]     // Halloween's window opens inside Fall's
    [InlineData(10, 31, "halloween")]
    [InlineData(11, 1, "thanksgiving")]  // and Thanksgiving's inside the rest of it
    [InlineData(11, 30, "thanksgiving")]
    [InlineData(12, 1, "better-christmas")]
    [InlineData(12, 25, "better-christmas")]
    [InlineData(12, 31, "better-christmas")]
    [InlineData(1, 1, "winter")]         // Christmas is over; Winter runs on
    [InlineData(2, 28, "winter")]
    public void The_narrower_window_wins_where_two_seasons_cover_the_same_day(int month, int day, string expected)
    {
        var pick = ThemeCatalog.SeasonalPick(new DateOnly(2026, month, day));

        Assert.NotNull(pick);
        Assert.Equal(expected, pick!.Id);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 20)]
    [InlineData(8, 31)]   // the day before Fall opens
    public void Out_of_season_there_is_nothing_to_pick(int month, int day)
    {
        Assert.Null(ThemeCatalog.SeasonalPick(new DateOnly(2026, month, day)));
    }

    [Fact]
    public void Plain_christmas_is_never_chosen_automatically()
    {
        // It covers exactly the same days as (Better)Christmas, so the shortest-window rule has no
        // way to separate them; the catalog resolves it by leaving this one out of the running. It
        // stays selectable by hand, which is the whole reason it is still in the catalog.
        for (var day = 1; day <= 31; day++)
        {
            Assert.NotEqual("christmas", ThemeCatalog.SeasonalPick(new DateOnly(2026, 12, day))?.Id);
        }

        Assert.NotNull(ThemeCatalog.Find("christmas"));
    }

    [Fact]
    public void Every_day_of_the_year_has_at_most_one_shortest_window()
    {
        // The rule has one failure mode: two automatic themes sharing a day *and* a window length,
        // where the winner would come down to catalog order and change under a reorder. Adding a
        // season that collides with an existing one fails here rather than silently.
        var date = new DateOnly(2024, 1, 1);   // a leap year, so 29 February is covered

        for (var i = 0; i < 366; i++, date = date.AddDays(1))
        {
            var candidates = ThemeCatalog.All
                .Where(t => t.Automatic && t.Season is not null && t.Season.Contains(date))
                .ToList();

            if (candidates.Count < 2) continue;

            var shortest = candidates.Min(t => t.Season!.LengthInDays);
            var tied = candidates.Where(t => t.Season!.LengthInDays == shortest).Select(t => t.Id).ToList();

            Assert.True(
                tied.Count == 1,
                $"{date:MMMM d} is claimed by {string.Join(" and ", tied)}, which share a window length.");
        }
    }

    [Theory]
    [InlineData(12, 1, 2, 29, 91)]    // Winter, and the wrap round the new year with it
    [InlineData(12, 1, 12, 31, 31)]   // December
    [InlineData(11, 1, 11, 30, 30)]   // November
    [InlineData(10, 1, 10, 31, 31)]   // October — the same length as December, which is fine:
                                      // they never overlap, so the rule is never asked to choose
    [InlineData(9, 1, 9, 1, 1)]       // a single day, both ends the same
    [InlineData(1, 1, 12, 31, 366)]   // the whole of a leap year, which is the yardstick
    public void A_window_measures_both_of_its_ends(int startMonth, int startDay, int endMonth, int endDay, int expected)
    {
        Assert.Equal(expected, new ThemeSeason(startMonth, startDay, endMonth, endDay).LengthInDays);
    }

    [Fact]
    public void The_next_season_is_the_one_the_settings_page_promises()
    {
        // Late August: nothing is in season, and Fall is what turns up.
        var next = ThemeCatalog.SeasonalNext(new DateOnly(2026, 8, 26));

        Assert.NotNull(next);
        Assert.Equal("fall", next!.Value.Theme.Id);
        Assert.Equal(new DateOnly(2026, 9, 1), next.Value.On);
    }

    [Fact]
    public void The_next_season_is_the_next_change_and_not_merely_the_next_window()
    {
        // Mid-October is Halloween. The next *change* is Thanksgiving on 1 November — not Fall,
        // whose window is already open and running underneath.
        var next = ThemeCatalog.SeasonalNext(new DateOnly(2026, 10, 15));

        Assert.NotNull(next);
        Assert.Equal("thanksgiving", next!.Value.Theme.Id);
        Assert.Equal(new DateOnly(2026, 11, 1), next.Value.On);
    }

    [Fact]
    public void Seasonal_is_offered_every_day_of_the_year()
    {
        // It is a rule, not a season, so the calendar must never take it out of the dropdown —
        // which is where a user who wants the seasons to arrive by themselves goes to say so.
        foreach (var month in Enumerable.Range(1, 12))
        {
            var offered = ThemeCatalog.Offered(new DateOnly(2026, month, 15), showAll: false);
            Assert.Contains(ThemeOptions.Seasonal, offered.Select(t => t.Id));
        }
    }

    [Fact]
    public void A_light_grounded_palette_is_listed_in_the_chevron_override()
    {
        // The dropdown chevron is a background image on <select>, so it cannot inherit
        // currentColor and has to be pre-coloured — once for dark grounds and once for light. That
        // second rule names its themes explicitly, and a new light palette that is not added to it
        // gets a pale grey chevron on a white field.
        var dashboard = ReadWeb("css", "dashboard.css");

        var chevronOverride = Regex.Match(
            dashboard,
            @"(?<selectors>(?::root\[data-theme=""[a-z-]+""\],?\s*)+)\{[^}]*--chevron-image");

        Assert.True(chevronOverride.Success, "dashboard.css has no light-ground --chevron-image override to check.");

        foreach (var theme in ThemeCatalog.All.Where(t => t.IsSeasonal))
        {
            var palette = File.ReadAllText(Path.Combine(WebRoot, "css", "themes", $"{theme.Id}.css"));
            if (!Regex.IsMatch(palette, @"color-scheme:\s*light")) continue;

            Assert.Contains($@"[data-theme=""{theme.Id}""]", chevronOverride.Groups["selectors"].Value);
        }
    }
}
