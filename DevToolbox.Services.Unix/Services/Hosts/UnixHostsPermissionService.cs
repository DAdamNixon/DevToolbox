using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Gives this user write access to the hosts file once, so later writes need no password — the Unix
/// version of the Windows grant, done with a POSIX ACL entry (<c>setfacl -m u:NAME:rw</c>) that
/// leaves the file's owner and mode alone and can be taken away again just as precisely.
/// </summary>
public sealed class UnixHostsPermissionService : IHostsPermissionService
{
    private readonly IHostsWriteBroker _broker;

    public UnixHostsPermissionService(IHostsWriteBroker broker)
    {
        _broker = broker;
    }

    public bool HasExplicitWriteAccess(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return false;
        if (UnixCommands.Resolve("getfacl") is not { } getfacl) return false;

        try
        {
            var (exitCode, output, _) = UnixCommands.RunAsync(getfacl, ["--omit-header", "--", targetPath])
                .GetAwaiter().GetResult();

            // "user:NAME:rw-", possibly followed by "#effective:..." when a mask narrows it.
            return exitCode == 0 && output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => line.StartsWith($"user:{Environment.UserName}:", StringComparison.Ordinal)
                             && line.Split(':')[2].Contains('w'));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException || UnixCommands.IsNotFound(ex))
        {
            return false;
        }
    }

    public Task<HostsWriteResult> GrantAsync(string targetPath, CancellationToken cancellationToken = default) =>
        ChangeAsync(targetPath, HostsWriteOperations.GrantWrite, cancellationToken);

    public Task<HostsWriteResult> RevokeAsync(string targetPath, CancellationToken cancellationToken = default) =>
        ChangeAsync(targetPath, HostsWriteOperations.RevokeWrite, cancellationToken);

    private Task<HostsWriteResult> ChangeAsync(string targetPath, string operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        return _broker.RunElevatedAsync(new HostsWriteRequest
        {
            Operation = operation,
            TargetPath = targetPath,
            // The field is named for Windows, where it carries a SID. Here it is the user name.
            PrincipalSid = Environment.UserName,
            RequestedAtUtc = DateTime.UtcNow,
        }, payload: null, cancellationToken);
    }
}
