using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models;

/// <summary>
/// Root of Config/ui_settings.yaml — app-wide preferences: how the window looks, and the few
/// defaults that decide what a fresh dialog or a freshly scanned card starts out as.
/// <para>
/// Separate from <see cref="AppSettings"/>, which binds appsettings.json and ships
/// with the build. This is user data: it lives in AppData beside every other
/// DevToolbox config and is meant to be hand-editable.
/// </para>
/// </summary>
public class UiSettings
{
    /// <summary>
    /// Any id from <see cref="ThemeCatalog"/> — <c>system</c>, <c>dark</c>, <c>light</c> or a
    /// seasonal one such as <c>christmas</c>. Anything else is treated as <c>system</c> rather than
    /// rejected, so a typo in a hand-edited file degrades to following the OS instead of failing to
    /// start.
    /// <para>
    /// A seasonal theme is kept here all year and only painted inside its window; see
    /// <see cref="ThemeCatalog"/> for why the setting is not erased when the season ends.
    /// </para>
    /// </summary>
    public string Theme { get; set; } = ThemeOptions.System;

    /// <summary>
    /// Whether a theme that has an animation — falling leaves, snow — is allowed to run it.
    /// <para>
    /// On by default, because the only themes that animate are seasonal ones and picking Fall is
    /// already the opt-in. An OS-level "reduce motion" overrides this regardless, in
    /// css/themeEffects.css.
    /// </para>
    /// </summary>
    public bool ThemeAnimations { get; set; } = true;

    /// <summary>
    /// Offer every seasonal theme year-round instead of only in its own window. Off by default: the
    /// point of the seasonal themes is that they turn up on their own.
    /// </summary>
    public bool ShowAllThemes { get; set; }

    /// <summary>
    /// Where the folder pickers start when you add a workspace, a location or a scan folder.
    /// Empty means no opinion, which is what every picker did before: they opened on This PC and
    /// left you to navigate to the same parent directory every time.
    /// <para>
    /// A hint, never a constraint — nothing validates against it and nothing is confined to it, so
    /// a path that has since been moved or unmounted costs you one navigation and no errors.
    /// </para>
    /// </summary>
    public string DefaultWorkspaceLocation { get; set; } = string.Empty;

    /// <summary>
    /// Whether the dashboard's group cards start open. Off matches the behaviour this has always
    /// had, which suits a lot of groups; on suits a few groups you always want to see inside.
    /// <para>
    /// A default and nothing more: it decides what a group looks like before anyone has touched it,
    /// and every card you expand or collapse by hand overrides it for the rest of the session.
    /// </para>
    /// </summary>
    public bool ExpandGroupsByDefault { get; set; }

    /// <summary>
    /// How many of a workspace's locations get a one-click Open button on the collapsed card.
    /// Three covers the common shape — a dev checkout, a demo checkout and not much else —
    /// and 0 turns them off for anyone who would rather have the narrower card.
    /// <para>
    /// Clamped to 0–5 on read by <see cref="QuickOpenButtonCount"/>: this file is hand-editable
    /// and a card with forty buttons on it is not a card.
    /// </para>
    /// </summary>
    public int QuickOpenButtons { get; set; } = 3;

    /// <summary>
    /// <see cref="QuickOpenButtons"/> brought inside the range the dashboard can render.
    /// </summary>
    [YamlIgnore]
    public int QuickOpenButtonCount => Math.Clamp(QuickOpenButtons, 0, 5);
}

/// <summary>
/// The themes that are always on offer, whatever the date. Named constants because they are
/// referenced by name in markup and in <see cref="ThemeCatalog"/>; the individual seasonal ids are
/// only ever looked up from the catalog, so they do not need one.
/// </summary>
public static class ThemeOptions
{
    /// <summary>
    /// Follow the calendar: the theme resolves by date rather than being one palette. The only
    /// setting that ever changes what is painted without the user touching it.
    /// </summary>
    public const string Seasonal = "seasonal";

    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Every theme id, seasonal ones included. The full definitions are in
    /// <see cref="ThemeCatalog.All"/>.</summary>
    public static IReadOnlyList<string> All => ThemeCatalog.All.Select(t => t.Id).ToList();

    /// <summary>Maps any input onto a supported value, defaulting to <see cref="System"/>.</summary>
    public static string Normalize(string? value) => ThemeCatalog.Normalize(value);
}
