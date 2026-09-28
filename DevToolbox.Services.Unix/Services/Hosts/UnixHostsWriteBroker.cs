using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Writes the hosts file on Linux: in place when this user already may, and otherwise through
/// <c>pkexec</c>, which shows the desktop's own administrator prompt.
/// <para>
/// The same contract as the Windows broker. A write only happens if the file still hashes to what
/// the page read (so a change made in an editor meanwhile is never overwritten), and it is read back
/// afterwards and compared with what was meant to be written.
/// </para>
/// <para>
/// The elevated step is a few lines of POSIX shell, run as root by pkexec and given only file paths
/// and hashes. It checks the staged file against the hash the app computed, checks the hosts file
/// is still the one that was read, and swaps the new file in with a rename so nobody can see it half
/// written. It runs nothing from configuration and touches no path but the two it is handed. That is
/// the Unix equivalent of <c>HostsElevatedCommands</c>, which is the only code that runs as
/// administrator on Windows, and it is kept as small for the same reason.
/// </para>
/// </summary>
public sealed class UnixHostsWriteBroker : IHostsWriteBroker
{
    /// <summary>pkexec's exit code when the prompt was dismissed or the password refused.</summary>
    private const int PkexecNotAuthorized = 126;

    /// <summary>pkexec's exit code when it could not authenticate at all, for example with no agent running.</summary>
    private const int PkexecFailed = 127;

    private const int WriteAttempts = 3;
    private const int RetryDelayMilliseconds = 100;

    /// <summary>How long to wait for another DevToolbox to finish writing before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The elevated step. Arguments: payload, target, expected payload hash, expected original hash.
    /// Exit codes are <see cref="HostsWriterExitCodes"/>, so the result reads the same as on Windows.
    /// Permissions and ownership are copied from the old file before the rename, so the new
    /// /etc/hosts is exactly as readable as the one it replaces.
    /// </summary>
    internal const string ElevatedScript = """
        set -u
        payload=$1 target=$2 want_payload=$3 want_original=$4
        [ -f "$target" ] && [ -f "$payload" ] || exit 5
        hash() { sha256sum < "$1" | cut -d' ' -f1; }
        [ "$(hash "$payload")" = "$want_payload" ] || exit 2
        [ "$(hash "$target")" = "$want_original" ] || exit 3
        tmp="$target.dtb.tmp"
        if ! { cp -- "$payload" "$tmp" && chmod --reference="$target" -- "$tmp" && chown --reference="$target" -- "$tmp" && mv -f -- "$tmp" "$target"; }; then
            rm -f -- "$tmp"
            exit 4
        fi
        [ "$(hash "$target")" = "$want_payload" ] || exit 6
        exit 0
        """;

    public bool CanWriteInProcess(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return false;

        try
        {
            // Opened for writing and closed again without touching the length, so this asks the
            // question without answering it destructively.
            using var probe = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public async Task<HostsWriteResult> WriteAsync(
        string targetPath,
        byte[] content,
        string expectedOriginalSha256,
        string? restoreFromPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        using var held = await WriteLock.AcquireAsync(LockTimeout, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Failed,
                "Another DevToolbox window is writing the hosts file. Try again in a moment.");
        }

        if (!File.Exists(targetPath))
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, $"{targetPath} does not exist.");
        }

        var current = HostsDocument.HashOf(await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false));
        if (!Matches(current, expectedOriginalSha256))
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Conflict,
                "The hosts file changed on disk since this page loaded, so nothing was written. Reload and try again.");
        }

        if (CanWriteInProcess(targetPath))
        {
            return await WriteDirectlyAsync(targetPath, content, restoreFromPath, cancellationToken).ConfigureAwait(false);
        }

        var result = await RunElevatedAsync(new HostsWriteRequest
        {
            Operation = HostsWriteOperations.Write,
            TargetPath = targetPath,
            PayloadSha256 = HostsDocument.HashOf(content),
            OriginalSha256 = expectedOriginalSha256,
            RequestedAtUtc = DateTime.UtcNow,
        }, content, cancellationToken).ConfigureAwait(false);

        return result.Outcome == HostsWriteOutcome.Written ? result with { Outcome = HostsWriteOutcome.WrittenElevated } : result;
    }

    /// <summary>
    /// Truncates the file in place and writes over it, then reads it back. In place rather than a
    /// rename because write access to a file says nothing about its directory, and a failed
    /// read-back puts the backup straight back.
    /// </summary>
    private static async Task<HostsWriteResult> WriteDirectlyAsync(
        string targetPath,
        byte[] content,
        string? restoreFromPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.SetLength(0);
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                return HostsWriteResult.Fail(HostsWriteOutcome.Denied, ex.Message);
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                await Task.Delay(RetryDelayMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return HostsWriteResult.Fail(HostsWriteOutcome.Failed, ex.Message);
            }
        }

        var written = HostsDocument.HashOf(await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false));
        if (Matches(written, HostsDocument.HashOf(content)))
        {
            return new HostsWriteResult(HostsWriteOutcome.Written, null, written);
        }

        var restored = await TryRestoreAsync(targetPath, restoreFromPath, cancellationToken).ConfigureAwait(false);
        return HostsWriteResult.Fail(
            HostsWriteOutcome.VerifyFailed,
            "The hosts file does not match what was written. "
            + (restored ? "The backup taken beforehand has been put back." : "The backup could not be put back — check the file by hand."));
    }

    /// <summary>
    /// Runs one request as root through pkexec. A write stages its payload under the app's own
    /// folder first; a grant or revoke is one <c>setfacl</c> naming the user in
    /// <see cref="HostsWriteRequest.PrincipalSid"/>, which on Unix carries a user name.
    /// </summary>
    public async Task<HostsWriteResult> RunElevatedAsync(
        HostsWriteRequest request,
        byte[]? payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (OperatingSystem.IsMacOS())
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Failed,
                "Writing the hosts file with administrator rights is not built for macOS yet. " +
                "Make /etc/hosts writable for your account, or edit it with sudo.");
        }

        if (UnixCommands.Resolve("pkexec") is not { } pkexec)
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Failed,
                "The hosts file needs administrator rights to change, and pkexec (polkit) is not installed to ask for them.");
        }

        var target = Path.GetFullPath(request.TargetPath);
        string? directory = null;

        try
        {
            string[] arguments;

            switch (request.Operation)
            {
                case HostsWriteOperations.Write:
                    if (payload is null) return HostsWriteResult.Fail(HostsWriteOutcome.Failed, "Nothing to write.");

                    directory = HostsPaths.CreateRequestDirectory(Guid.NewGuid());
                    var payloadPath = Path.Combine(directory, HostsPaths.PayloadFileName);
                    await File.WriteAllBytesAsync(payloadPath, payload, cancellationToken).ConfigureAwait(false);

                    arguments = ["/bin/sh", "-c", ElevatedScript, "devtoolbox-hosts",
                                 payloadPath, target, request.PayloadSha256.ToLowerInvariant(), request.OriginalSha256.ToLowerInvariant()];
                    break;

                case HostsWriteOperations.GrantWrite or HostsWriteOperations.RevokeWrite:
                    if (string.IsNullOrWhiteSpace(request.PrincipalSid) || UnixCommands.Resolve("setfacl") is not { } setfacl)
                    {
                        return HostsWriteResult.Fail(HostsWriteOutcome.Failed, "Changing permissions needs setfacl (the acl package) and a user name.");
                    }

                    arguments = request.Operation == HostsWriteOperations.GrantWrite
                        ? [setfacl, "-m", $"u:{request.PrincipalSid}:rw", "--", target]
                        : [setfacl, "-x", $"u:{request.PrincipalSid}", "--", target];
                    break;

                default:
                    return HostsWriteResult.Fail(HostsWriteOutcome.Failed, $"Unknown operation '{request.Operation}'.");
            }

            var (exitCode, _, error) = await UnixCommands.RunAsync(pkexec, arguments, cancellationToken).ConfigureAwait(false);

            if (exitCode == HostsWriterExitCodes.Success && request.Operation == HostsWriteOperations.Write)
            {
                // Read back here as well: the file is readable without elevation, and this is the
                // hash the caller records.
                var verified = HostsDocument.HashOf(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false));
                return new HostsWriteResult(HostsWriteOutcome.Written, null, verified);
            }

            return Interpret(exitCode, error.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException || UnixCommands.IsNotFound(ex))
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, ex.Message);
        }
        finally
        {
            // The request directory holds a whole copy of the hosts file, so it does not linger.
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left behind at worst */ }
            }
        }
    }

    private static HostsWriteResult Interpret(int exitCode, string detail) => exitCode switch
    {
        HostsWriterExitCodes.Success => new HostsWriteResult(HostsWriteOutcome.Written, null, null),

        PkexecNotAuthorized or PkexecFailed => HostsWriteResult.Fail(
            HostsWriteOutcome.Declined,
            "Administrator rights were not given, so the hosts file was not changed."),

        HostsWriterExitCodes.TargetChanged => HostsWriteResult.Fail(
            HostsWriteOutcome.Conflict,
            "The hosts file changed while the password prompt was open, so nothing was written."),

        HostsWriterExitCodes.Denied => HostsWriteResult.Fail(
            HostsWriteOutcome.Denied,
            "The hosts file could not be replaced even as root" + (detail.Length > 0 ? $": {detail}" : ".")),

        HostsWriterExitCodes.VerifyFailed => HostsWriteResult.Fail(
            HostsWriteOutcome.VerifyFailed,
            "The hosts file does not match what was written."),

        HostsWriterExitCodes.PayloadMismatch => HostsWriteResult.Fail(
            HostsWriteOutcome.Failed,
            "The staged file does not match the request. It may have been written incompletely."),

        _ => HostsWriteResult.Fail(
            HostsWriteOutcome.Failed,
            detail.Length > 0 ? detail : $"The elevated step exited with code {exitCode}."),
    };

    private static async Task<bool> TryRestoreAsync(string targetPath, string? restoreFromPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(restoreFromPath) || !File.Exists(restoreFromPath)) return false;

        try
        {
            var original = await File.ReadAllBytesAsync(restoreFromPath, cancellationToken).ConfigureAwait(false);
            await using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(0);
            await stream.WriteAsync(original, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One writer at a time across every DevToolbox on the machine, held as an exclusive open of a
    /// lock file. The kernel drops it when the process dies, so a crash mid-write cannot leave
    /// everyone else locked out — the property the Windows broker's named mutex is chosen for.
    /// </summary>
    private static class WriteLock
    {
        public static async Task<IDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(HostsPaths.AppDataRoot);
            var path = Path.Combine(HostsPaths.AppDataRoot, "hosts-write.lock");
            var deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
    }
}
