using System.Globalization;

namespace DevToolbox.Services.Models;

/// <summary>
/// The window of the year a seasonal theme is offered in, to the day.
/// <para>
/// There is no year here on purpose. A season is a recurring window, not a date range, so a theme
/// picked one December is still Christmas the next — see <see cref="ThemeCatalog"/> for why that
/// matters to what gets saved.
/// </para>
/// </summary>
/// <param name="StartMonth">1-12.</param>
/// <param name="StartDay">Day of <paramref name="StartMonth"/>.</param>
/// <param name="EndMonth">1-12. May be earlier than <paramref name="StartMonth"/>, which means the
/// window wraps the new year — Winter runs 1 December to 29 February.</param>
/// <param name="EndDay">Day of <paramref name="EndMonth"/>, inclusive.</param>
public sealed record ThemeSeason(int StartMonth, int StartDay, int EndMonth, int EndDay)
{
    /// <summary>Month and day as one sortable number, so a comparison needs no year.</summary>
    private static int Key(int month, int day) => (month * 100) + day;

    public bool Contains(DateOnly date)
    {
        var today = Key(date.Month, date.Day);
        var from = Key(StartMonth, StartDay);
        var to = Key(EndMonth, EndDay);

        // A window whose end sorts before its start straddles 1 January, and is
        // the union of two ranges rather than the intersection of one.
        return from <= to
            ? today >= from && today <= to
            : today >= from || today <= to;
    }

    /// <summary>
    /// The form <c>js/themeCatalog.js</c> stores this window in. Compared by
    /// <c>ThemeCatalogTests</c>, which is the only thing keeping the two catalogs honest.
    /// </summary>
    public string Wire => $"{StartMonth:00}-{StartDay:00}..{EndMonth:00}-{EndDay:00}";

    /// <summary>For the Settings hint: "Sep 1 – Nov 30".</summary>
    public string Describe()
    {
        var months = CultureInfo.CurrentCulture.DateTimeFormat;
        return $"{months.GetAbbreviatedMonthName(StartMonth)} {StartDay} – {months.GetAbbreviatedMonthName(EndMonth)} {EndDay}";
    }
}

/// <summary>One theme, as the UI needs to know it.</summary>
/// <param name="Id">Matches the <c>data-theme</c> value, the CSS file name under
/// <c>wwwroot/css/themes/</c>, and the id in <c>js/themeCatalog.js</c>.</param>
/// <param name="Label">What the dropdown shows.</param>
/// <param name="Season">When it is offered, or <c>null</c> for one that always is.</param>
/// <param name="Effect">The animation it runs — <c>snow</c>, <c>leaves</c>, <c>bats</c> — or
/// <c>null</c> for a still theme. Only ever honoured when the user has animations enabled.</param>
public sealed record ThemeDefinition(string Id, string Label, ThemeSeason? Season, string? Effect)
{
    public bool IsSeasonal => Season is not null;

    public bool IsAvailableOn(DateOnly date) => Season is null || Season.Contains(date);
}

/// <summary>
/// Every theme the app has, and the rules about when each one is on offer.
/// <para>
/// This is deliberately a second copy of <c>wwwroot/js/themeCatalog.js</c>. The palette has to be
/// on <c>&lt;html&gt;</c> before the first paint, which happens long before any of this code has
/// read a config file, so the boot script cannot ask .NET what the themes are — it needs its own
/// list. <c>ThemeCatalogTests</c> compares the two, and the CSS files, so the duplication is
/// checked rather than trusted.
/// </para>
/// <para>
/// The seasonal rule is about what is <em>offered and painted</em>, never about what is stored.
/// Choosing Christmas in December writes <c>christmas</c> to ui_settings.yaml and leaves it there:
/// January paints the default instead, and the following December it comes back on its own. Erasing
/// the choice at the end of the season would mean the setting silently changed itself, which is
/// worse than a theme that waits.
/// </para>
/// </summary>
public static class ThemeCatalog
{
    /// <summary>
    /// In the order the dropdown shows them: the three that are always there, then the seasonal ones
    /// in calendar order. Keep in step with <c>js/themeCatalog.js</c> and <c>css/themes.css</c>.
    /// </summary>
    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        new ThemeDefinition(ThemeOptions.System, "System Default", null, null),
        new ThemeDefinition(ThemeOptions.Dark, "Dark Theme", null, null),
        new ThemeDefinition(ThemeOptions.Light, "Light Theme", null, null),

        new ThemeDefinition("fall", "Fall", new ThemeSeason(9, 1, 11, 30), "leaves"),
        new ThemeDefinition("halloween", "Halloween", new ThemeSeason(10, 1, 10, 31), "bats"),
        new ThemeDefinition("thanksgiving", "Thanksgiving", new ThemeSeason(11, 1, 11, 30), "leaves"),
        new ThemeDefinition("winter", "Winter", new ThemeSeason(12, 1, 2, 29), "snow"),
        new ThemeDefinition("christmas", "Christmas", new ThemeSeason(12, 1, 12, 31), "snow"),
    };

    /// <summary>
    /// A phrase for an effect id, for a sentence like "This theme animates falling snow". A new
    /// effect adds a case here; one without a case reads as a generic animation rather than as a
    /// broken string, because the id itself ("bats") is not a phrase.
    /// </summary>
    public static string DescribeEffect(string? effect) => effect switch
    {
        "snow" => "falling snow",
        "leaves" => "falling leaves",
        "bats" => "bats crossing the window",
        _ => "an animation",
    };

    public static ThemeDefinition? Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps any input onto a real theme id, defaulting to <c>system</c>. Seasons are not consulted:
    /// an out-of-season id is a perfectly valid thing to have saved.
    /// </summary>
    public static string Normalize(string? id) => Find(id)?.Id ?? ThemeOptions.System;

    /// <summary>The themes to put in the dropdown.</summary>
    /// <param name="today">Injected rather than read from the clock so the rule is testable.</param>
    /// <param name="showAll">The user's "show all themes" setting — when on, the calendar is
    /// ignored and everything is offered.</param>
    public static IEnumerable<ThemeDefinition> Offered(DateOnly today, bool showAll) =>
        All.Where(t => showAll || t.IsAvailableOn(today));

    /// <summary>
    /// Whether a saved theme is the one currently being painted. False for a seasonal choice that is
    /// waiting for its season, which is the case Settings has to explain rather than hide.
    /// </summary>
    public static bool IsActive(string? id, DateOnly today, bool showAll)
    {
        var theme = Find(id);
        return theme is not null && (showAll || theme.IsAvailableOn(today));
    }
}
