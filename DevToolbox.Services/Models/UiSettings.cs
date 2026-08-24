namespace DevToolbox.Services.Models;

/// <summary>
/// Root of Config/ui_settings.yaml — app-wide presentation preferences.
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
}

/// <summary>
/// The three themes that are always on offer. Named constants because they are referenced by name
/// in markup and in <see cref="ThemeCatalog"/>; the seasonal ids are only ever looked up from the
/// catalog, so they do not need one.
/// </summary>
public static class ThemeOptions
{
    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Every theme id, seasonal ones included. The full definitions are in
    /// <see cref="ThemeCatalog.All"/>.</summary>
    public static IReadOnlyList<string> All => ThemeCatalog.All.Select(t => t.Id).ToList();

    /// <summary>Maps any input onto a supported value, defaulting to <see cref="System"/>.</summary>
    public static string Normalize(string? value) => ThemeCatalog.Normalize(value);
}
