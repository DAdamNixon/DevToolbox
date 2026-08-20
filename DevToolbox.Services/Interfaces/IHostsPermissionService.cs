using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Grants or removes the current account's write access to the hosts file.
/// <para>
/// One elevation prompt, once, in exchange for never seeing another when switching environments —
/// which is what makes flipping a group from the tray pleasant rather than a chore. It does loosen
/// the protection on a system file, so it is off by default, it must be asked for explicitly, and
/// it must be reversible.
/// </para>
/// </summary>
public interface IHostsPermissionService
{
    /// <summary>
    /// Whether this account has been given its own write entry on the file, as opposed to whatever
    /// it inherits. This is what the Settings page shows as the current state.
    /// </summary>
    bool HasExplicitWriteAccess(string targetPath);

    Task<HostsWriteResult> GrantAsync(string targetPath, CancellationToken cancellationToken = default);

    Task<HostsWriteResult> RevokeAsync(string targetPath, CancellationToken cancellationToken = default);
}
