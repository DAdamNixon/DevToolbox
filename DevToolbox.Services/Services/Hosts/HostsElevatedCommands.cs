using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// The elevated half of a hosts-file write. Runs in a second instance of this application started
/// with <see cref="RequestSwitch"/> and the "runas" verb, then exits.
/// <para>
/// This is the only code in DevToolbox that ever runs as administrator, so it is kept as small as
/// it can be and does exactly three things: copy a staged file over the hosts file, or add or
/// remove one access-control entry. It runs no command from configuration, touches no path it was
/// not told to, and never creates a hosts file that does not already exist.
/// </para>
/// </summary>
public static class HostsElevatedCommands
{
    /// <summary>Command-line switch that puts the application into elevated-write mode.</summary>
    public const string RequestSwitch = "--hosts-request";



    /// <summary>
    /// Whether these arguments ask for an elevated write, and where the request lives.
    /// Called before any UI is created, so a mistyped argument simply falls through to a normal start.
    /// </summary>
    public static bool TryGetRequestDirectory(string[] args, out string directory)
    {
        directory = string.Empty;
        if (args is null) return false;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], RequestSwitch, StringComparison.OrdinalIgnoreCase)) continue;

            directory = args[i + 1];
            return !string.IsNullOrWhiteSpace(directory);
        }

        return false;
    }

    /// <summary>
    /// Carries out the request and writes <c>result.json</c> beside it.
    /// </summary>
    /// <returns>One of <see cref="HostsWriterExitCodes"/>.</returns>
    public static int Execute(string requestDirectory)
    {
        string? message = null;
        string? verified = null;
        var exitCode = HostsWriterExitCodes.Unexpected;

        try
        {
            (exitCode, message, verified) = Run(requestDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or ArgumentException or InvalidOperationException)
        {
            message = ex.Message;
        }

        TryWriteResult(requestDirectory, exitCode, message, verified);

        return exitCode;
    }

    private static (int ExitCode, string? Message, string? Verified) Run(string requestDirectory)
    {
        if (!HostsPaths.IsInsideRequestRoot(requestDirectory))
        {
            return (HostsWriterExitCodes.MalformedRequest,
                    "The request directory is not inside the application's own staging folder.",
                    null);
        }

        var requestPath = Path.Combine(requestDirectory, HostsPaths.RequestFileName);
        if (!File.Exists(requestPath))
        {
            return (HostsWriterExitCodes.MalformedRequest, "No request file was found.", null);
        }

        var request = JsonSerializer.Deserialize<HostsWriteRequest>(File.ReadAllText(requestPath), HostsWriteJson.Options);
        if (request is null || request.SchemaVersion != 1)
        {
            return (HostsWriterExitCodes.MalformedRequest, "The request file could not be read.", null);
        }

        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            return (HostsWriterExitCodes.MalformedRequest,
                    "The request does not name a file to write.",
                    null);
        }

        // The target must already exist. Creating a hosts file where none is present needs rights in
        // the directory, and is not something this tool should ever do quietly.
        var target = Path.GetFullPath(request.TargetPath);
        if (!File.Exists(target))
        {
            return (HostsWriterExitCodes.MalformedRequest, $"{target} does not exist.", null);
        }

        return request.Operation switch
        {
            HostsWriteOperations.Write => Write(requestDirectory, request, target),
            HostsWriteOperations.GrantWrite => SetAccess(request, target, grant: true),
            HostsWriteOperations.RevokeWrite => SetAccess(request, target, grant: false),
            _ => (HostsWriterExitCodes.MalformedRequest, $"Unknown operation '{request.Operation}'.", null),
        };
    }

    private static (int ExitCode, string? Message, string? Verified) Write(
        string requestDirectory,
        HostsWriteRequest request,
        string target)
    {
        var payloadPath = Path.Combine(requestDirectory, HostsPaths.PayloadFileName);
        if (!File.Exists(payloadPath))
        {
            return (HostsWriterExitCodes.MalformedRequest, "No payload file was found.", null);
        }

        var payload = File.ReadAllBytes(payloadPath);
        if (!Matches(HostsDocument.HashOf(payload), request.PayloadSha256))
        {
            return (HostsWriterExitCodes.PayloadMismatch,
                    "The staged file does not match the request. It may have been written incompletely.",
                    null);
        }

        // Refuse a stale write. Between reading the file and this prompt being answered, somebody
        // could have saved it in Notepad; overwriting that silently is worse than failing.
        var current = HostsDocument.HashOf(File.ReadAllBytes(target));
        if (!Matches(current, request.OriginalSha256))
        {
            return (HostsWriterExitCodes.TargetChanged,
                    "The hosts file changed since it was read, so it was left alone.",
                    null);
        }

        // No backup is taken here. Reading the hosts file needs no elevation, so the caller has
        // already copied it — keeping this side to the smallest possible job.

        // Written to a temporary file in the same directory and then moved, so the replacement is
        // atomic and a failure part-way through cannot leave a half-written hosts file.
        var temporary = target + ".dtb.tmp";

        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, target, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temporary);
            return (HostsWriterExitCodes.Denied, ex.Message, null);
        }
        catch (IOException ex)
        {
            TryDelete(temporary);
            return (HostsWriterExitCodes.Denied, ex.Message, null);
        }

        var written = HostsDocument.HashOf(File.ReadAllBytes(target));
        if (!Matches(written, request.PayloadSha256))
        {
            return (HostsWriterExitCodes.VerifyFailed,
                    "The hosts file does not match what was written.",
                    written);
        }

        return (HostsWriterExitCodes.Success, null, written);
    }

    /// <summary>
    /// Adds or removes a single Modify entry for one account, named by SID.
    /// <para>
    /// A SID rather than a display name because account names are localised and domain-qualified,
    /// and getting that wrong on a system file's permissions is not a mistake worth risking.
    /// </para>
    /// </summary>
    private static (int ExitCode, string? Message, string? Verified) SetAccess(
        HostsWriteRequest request,
        string target,
        bool grant)
    {
        if (string.IsNullOrWhiteSpace(request.PrincipalSid))
        {
            return (HostsWriterExitCodes.MalformedRequest, "No account was named.", null);
        }

        SecurityIdentifier identity;
        try
        {
            identity = new SecurityIdentifier(request.PrincipalSid);
        }
        catch (ArgumentException ex)
        {
            return (HostsWriterExitCodes.MalformedRequest, ex.Message, null);
        }

        try
        {
            var file = new FileInfo(target);
            var security = file.GetAccessControl();
            var rule = new FileSystemAccessRule(identity, FileSystemRights.Modify, AccessControlType.Allow);

            if (grant) security.AddAccessRule(rule);
            else security.RemoveAccessRuleSpecific(rule);

            file.SetAccessControl(security);
        }
        // Ordered: PrivilegeNotHeldException derives from UnauthorizedAccessException, and it means
        // something more specific — the process lacks a privilege rather than the file refusing us.
        catch (PrivilegeNotHeldException ex)
        {
            return (HostsWriterExitCodes.Denied, $"A required privilege is not held: {ex.Message}", null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (HostsWriterExitCodes.Denied, ex.Message, null);
        }

        return (HostsWriterExitCodes.Success, null, null);
    }

    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is untidy, not harmful.
        }
    }

    private static void TryWriteResult(string requestDirectory, int exitCode, string? message, string? verified)
    {
        try
        {
            var response = new HostsWriteResponse
            {
                ExitCode = exitCode,
                Message = message,
                VerifiedSha256 = verified,
                CompletedAtUtc = DateTime.UtcNow,
            };

            File.WriteAllText(
                Path.Combine(requestDirectory, HostsPaths.ResultFileName),
                JsonSerializer.Serialize(response, HostsWriteJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit code is the primary channel; the caller falls back to it.
        }
    }
}
