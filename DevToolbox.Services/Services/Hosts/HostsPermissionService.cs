using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <inheritdoc cref="IHostsPermissionService"/>
public sealed class HostsPermissionService : IHostsPermissionService
{
    private readonly IHostsWriteBroker _broker;

    public HostsPermissionService(IHostsWriteBroker broker)
    {
        _broker = broker;
    }

    public bool HasExplicitWriteAccess(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return false;

        var identity = CurrentUserSid();
        if (identity is null) return false;

        try
        {
            var security = new FileInfo(targetPath).GetAccessControl();

            // includeInherited: false — an inherited right is whatever the machine hands everybody,
            // and is not something this application granted or can revoke.
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!identity.Equals(rule.IdentityReference)) continue;
                if ((rule.FileSystemRights & FileSystemRights.Write) != 0) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Not being able to read the permissions is not the same as not having them, but the
            // honest answer to show the user is "no explicit grant found".
            return false;
        }
    }

    public Task<HostsWriteResult> GrantAsync(string targetPath, CancellationToken cancellationToken = default) =>
        ChangeAccessAsync(targetPath, HostsWriteOperations.GrantWrite, cancellationToken);

    public Task<HostsWriteResult> RevokeAsync(string targetPath, CancellationToken cancellationToken = default) =>
        ChangeAccessAsync(targetPath, HostsWriteOperations.RevokeWrite, cancellationToken);

    private async Task<HostsWriteResult> ChangeAccessAsync(
        string targetPath,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var identity = CurrentUserSid();
        if (identity is null)
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Failed,
                "Could not work out which Windows account is signed in.");
        }

        var request = new HostsWriteRequest
        {
            Operation = operation,
            TargetPath = targetPath,

            // A SID rather than a display name: account names are localised and domain-qualified,
            // and getting one wrong while editing a system file's permissions is not a mistake worth
            // risking.
            PrincipalSid = identity.Value,
            RequestedAtUtc = DateTime.UtcNow,
        };

        // No payload — this changes permissions, not content.
        return await _broker.RunElevatedAsync(request, payload: null, cancellationToken).ConfigureAwait(false);
    }

    private static SecurityIdentifier? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
