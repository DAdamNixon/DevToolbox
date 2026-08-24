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

    private sealed record JsTheme(string Id, string? Season, string? Effect);

    /// <summary>
    /// Parses <c>themeCatalog.js</c> with a regex rather than a JS engine. That is why the file is
    /// documented as one theme per line in a fixed field order: the format is a contract with this
    /// method, and a reformat that breaks it should break here rather than at runtime.
    /// </summary>
    private static List<JsTheme> ParseJsCatalog()
    {
        var source = ReadWeb("js", "themeCatalog.js");

        var entries = Regex.Matches(
            source,
            @"\{\s*id:\s*'(?<id>[a-z]+)',\s*season:\s*(?:null|'(?<season>[^']*)'),\s*effect:\s*(?:null|'(?<effect>[^']*)')\s*\}");

        Assert.NotEmpty(entries);

        return entries.Select(m => new JsTheme(
            m.Groups["id"].Value,
            m.Groups["season"].Success ? m.Groups["season"].Value : null,
            m.Groups["effect"].Success ? m.Groups["effect"].Value : null)).ToList();
    }

    [Fact]
    public void The_javascript_catalog_lists_the_same_themes_in_the_same_order()
    {
        var fromJs = ParseJsCatalog();
        var fromCs = ThemeCatalog.All
            .Select(t => new JsTheme(t.Id, t.Season?.Wire, t.Effect))
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
    public void Out_of_season_only_the_three_standard_themes_are_offered()
    {
        var july = new DateOnly(2026, 7, 4);

        Assert.Equal(
            new[] { ThemeOptions.System, ThemeOptions.Dark, ThemeOptions.Light },
            ThemeCatalog.Offered(july, showAll: false).Select(t => t.Id));
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
        Assert.Equal(ThemeOptions.System, ThemeOptions.Normalize("easter"));
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
}
