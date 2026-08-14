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
    /// <c>system</c>, <c>dark</c> or <c>light</c>. Anything else is treated as
    /// <c>system</c> rather than rejected, so a typo in a hand-edited file
    /// degrades to following the OS instead of failing to start.
    /// </summary>
    public string Theme { get; set; } = ThemeOptions.System;
}

/// <summary>The accepted values of <see cref="UiSettings.Theme"/>.</summary>
public static class ThemeOptions
{
    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    public static readonly IReadOnlyList<string> All = new[] { System, Dark, Light };

    /// <summary>Maps any input onto a supported value, defaulting to <see cref="System"/>.</summary>
    public static string Normalize(string? value) =>
        All.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? System;
}
