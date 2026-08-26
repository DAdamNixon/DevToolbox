using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Turns the folders declared in Config/workspaceSources.yaml into dashboard groups.
/// Results are virtual — they are rebuilt from disk on every scan and never persisted.
/// </summary>
public interface IWorkspaceSourceService : ICachedConfig
{
    /// <summary>Sources as configured on disk.</summary>
    Task<WorkspaceSourceConfig> GetConfigAsync();

    Task SaveConfigAsync(WorkspaceSourceConfig config);

    /// <summary>
    /// Groups produced by the last scan, scanning first if needed.
    /// </summary>
    Task<List<WorkspaceGroup>> GetGroupsAsync(bool forceRescan = false);

    /// <summary>Per-source diagnostics from the most recent scan.</summary>
    IReadOnlyList<SourceScanResult> LastScan { get; }

    /// <summary>
    /// What <paramref name="source"/> would put on the dashboard, without saving it or
    /// touching the live scan. The Scan Folders dialog calls this on every edit, so it
    /// samples the folder rather than enumerating all of it and runs off the UI thread.
    /// </summary>
    Task<SourcePreview> PreviewAsync(WorkspaceSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// The application a scanned location should open with, when its source declares one.
    /// Returns null for hand-added locations and for sources with no override.
    /// </summary>
    CustomOpenOption? GetOpenOptionFor(WorkspaceLocation location);
}
