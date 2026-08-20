using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// The Host Changer's own preferences, from <c>Config/host_changer_settings.yaml</c>.
/// </summary>
public interface IHostsSettingsService
{
    /// <summary>
    /// Why the settings file could not be read, or null. Surfaced in the UI instead of the file
    /// being replaced — a malformed config is reported, never overwritten.
    /// </summary>
    string? LoadError { get; }

    Task<HostsSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(HostsSettings settings, CancellationToken cancellationToken = default);
}
