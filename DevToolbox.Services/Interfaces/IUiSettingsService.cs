using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Reads and writes Config/ui_settings.yaml, the app-wide presentation preferences.
/// </summary>
public interface IUiSettingsService
{
    /// <summary>
    /// The current settings. Cached after the first read; never null, and never
    /// throws — an unreadable or malformed file yields defaults and sets
    /// <see cref="LoadError"/>.
    /// </summary>
    Task<UiSettings> GetAsync();

    /// <summary>Persists <paramref name="settings"/> and refreshes the cache.</summary>
    Task SaveAsync(UiSettings settings);

    /// <summary>
    /// Why the last load fell back to defaults, or null if it did not. Surfaced by
    /// the Settings page so a YAML typo is visible rather than silently reverting.
    /// </summary>
    string? LoadError { get; }
}
