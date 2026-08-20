using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Maps a file to the program that should open it, from Config/openHandlers.yaml.
/// </summary>
public interface IOpenHandlerService
{
    Task<OpenHandlerConfig> GetConfigAsync();

    Task SaveConfigAsync(OpenHandlerConfig config);

    /// <summary>
    /// The handler for a path, or null to fall back to the Windows file association.
    /// Call <see cref="GetConfigAsync"/> once before relying on this.
    /// </summary>
    CustomOpenOption? HandlerFor(string path);
}
