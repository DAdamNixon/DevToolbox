using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <inheritdoc cref="IHostsWriteBroker"/>
public sealed class HostsWriteBroker : IHostsWriteBroker
{
    /// <summary>Windows' code for "the user dismissed the elevation prompt".</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>Windows' code for "the file named could not be found".</summary>
    private const int ErrorFileNotFound = 2;

    /// <summary>
    /// Guards the write across every instance of DevToolbox on the machine, not just this one.
    /// Two windows open is normal; two windows writing the hosts file at once is not.
    /// </summary>
    public const string DefaultWriteLockName = @"Global\DevToolbox.HostsWrite";

    private const int WriteAttempts = 3;
    private const int RetryDelayMilliseconds = 100;

    /// <summary>How long to wait for another instance to finish before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _writeLockName;

    public HostsWriteBroker()
        : this(DefaultWriteLockName)
    {
    }

    /// <param name="writeLockName">
    /// The machine-wide lock's name. Only overridden by tests, which use a session-local name so
    /// they neither wait on a real DevToolbox nor need the privilege the <c>Global\</c> namespace
    /// asks for.
    /// </param>
    public HostsWriteBroker(string writeLockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writeLockName);

        _writeLockName = writeLockName;
    }

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

    public Task<HostsWriteResult> WriteAsync(
        string targetPath,
        byte[] content,
        string expectedOriginalSha256,
        string? restoreFromPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        return HoldingTheWriteLock(
            token => WriteLockedAsync(targetPath, content, expectedOriginalSha256, restoreFromPath, token),
            cancellationToken);
    }

    /// <summary>
    /// Runs the whole write on one dedicated thread, holding the machine-wide lock for its duration.
    /// <para>
    /// The single thread is the entire point. A named <see cref="Mutex"/> has thread affinity —
    /// Windows requires the thread that took it to be the thread that releases it — while an
    /// <c>await</c> resumes on whichever pool thread is free. Taking the lock, awaiting, and
    /// releasing in a <c>finally</c> therefore throws <see cref="ApplicationException"/>
    /// ("Object synchronization method was called from an unsynchronized block of code"), and the
    /// throw happens <em>instead of</em> the release — so the lock is also left held, and the next
    /// write waits the full timeout before failing. Keeping everything between the two calls on one
    /// thread removes the problem rather than working around it.
    /// </para>
    /// <para>
    /// A named <see cref="Semaphore"/> would sidestep the affinity rule and is the wrong trade: it
    /// has no notion of an abandoned holder, so a DevToolbox killed while its elevation prompt was
    /// open would lock every other instance out until the machine restarted.
    /// <see cref="AbandonedMutexException"/> is worth keeping.
    /// </para>
    /// </summary>
    private Task<HostsWriteResult> HoldingTheWriteLock(
        Func<CancellationToken, Task<HostsWriteResult>> write,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () =>
            {
                using var mutex = new Mutex(initiallyOwned: false, _writeLockName);
                var held = false;

                try
                {
                    try
                    {
                        held = mutex.WaitOne(LockTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        // Another instance died mid-write. We now hold it, and the hash precondition
                        // inside is what actually protects the file.
                        held = true;
                    }

                    if (!held)
                    {
                        return HostsWriteResult.Fail(
                            HostsWriteOutcome.Failed,
                            "Another DevToolbox window is writing the hosts file. Try again in a moment.");
                    }

                    // Blocking here is the reason this thread exists: it has to still be this thread
                    // when the release runs. Safe from deadlock because a thread started this way
                    // carries no synchronization context to marshal continuations back to.
                    return write(cancellationToken).GetAwaiter().GetResult();
                }
                finally
                {
                    if (held) mutex.ReleaseMutex();
                }
            },
            cancellationToken,

            // A write can sit on a UAC prompt for as long as the developer takes to answer it, which
            // is far too long to borrow a pool thread for.
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>The write itself, with the machine-wide lock already held.</summary>
    private async Task<HostsWriteResult> WriteLockedAsync(
        string targetPath,
        byte[] content,
        string expectedOriginalSha256,
        string? restoreFromPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, $"{targetPath} does not exist.");
        }

        var current = HostsDocument.HashOf(await File.ReadAllBytesAsync(targetPath, cancellationToken)
                                                     .ConfigureAwait(false));

        if (!Matches(current, expectedOriginalSha256))
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Conflict,
                "The hosts file changed on disk since this page loaded, so nothing was written. Reload and try again.");
        }

        return CanWriteInProcess(targetPath)
            ? await WriteDirectlyAsync(targetPath, content, restoreFromPath, cancellationToken).ConfigureAwait(false)
            : await WriteElevatedAsync(targetPath, content, expectedOriginalSha256, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Truncates the file in place and writes over it.
    /// <para>
    /// Not a temporary-file-and-rename, because that needs the right to create a file in
    /// <c>…\etc</c> — and a Modify entry on the hosts file itself grants nothing of the kind. So the
    /// UAC-free path has to write in place, which is not atomic. The backup taken beforehand plus
    /// the hash check afterwards are what make that safe: a failed verification puts the old file
    /// straight back.
    /// </para>
    /// </summary>
    private async Task<HostsWriteResult> WriteDirectlyAsync(
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
                return HostsWriteResult.Fail(HostsWriteOutcome.Denied, Explain(ex));
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                // Usually an editor holding the file open for a moment.
                await Task.Delay(RetryDelayMilliseconds * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return HostsWriteResult.Fail(HostsWriteOutcome.Failed, ex.Message);
            }
        }

        var written = HostsDocument.HashOf(await File.ReadAllBytesAsync(targetPath, cancellationToken)
                                                     .ConfigureAwait(false));

        if (Matches(written, HostsDocument.HashOf(content)))
        {
            return new HostsWriteResult(HostsWriteOutcome.Written, null, written);
        }

        var restored = await TryRestoreAsync(targetPath, restoreFromPath, cancellationToken).ConfigureAwait(false);

        return HostsWriteResult.Fail(
            HostsWriteOutcome.VerifyFailed,
            "The hosts file does not match what was written. "
            + (restored
                ? "The backup taken beforehand has been put back."
                : "The backup could not be put back — check the file by hand."));
    }

    private async Task<HostsWriteResult> WriteElevatedAsync(
        string targetPath,
        byte[] content,
        string expectedOriginalSha256,
        CancellationToken cancellationToken)
    {
        var request = new HostsWriteRequest
        {
            Operation = HostsWriteOperations.Write,
            TargetPath = targetPath,
            PayloadSha256 = HostsDocument.HashOf(content),
            OriginalSha256 = expectedOriginalSha256,
            RequestedAtUtc = DateTime.UtcNow,
        };

        var result = await RunElevatedAsync(request, content, cancellationToken).ConfigureAwait(false);

        return result.Outcome == HostsWriteOutcome.Written
            ? result with { Outcome = HostsWriteOutcome.WrittenElevated }
            : result;
    }

    public async Task<HostsWriteResult> RunElevatedAsync(
        HostsWriteRequest request,
        byte[]? payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Failed,
                "Could not work out where DevToolbox is running from, so it cannot ask for elevation.");
        }

        var directory = HostsPaths.CreateRequestDirectory(Guid.NewGuid());

        try
        {
            if (payload is not null)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, HostsPaths.PayloadFileName), payload, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(directory, HostsPaths.RequestFileName),
                JsonSerializer.Serialize(request, HostsWriteJson.Options),
                cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,

                // Required for "runas", and it rules out redirecting the child's streams — which is
                // exactly why the request travels as files in a directory instead of on the command
                // line.
                UseShellExecute = true,
                Verb = "runas",
            };

            startInfo.ArgumentList.Add(HostsElevatedCommands.RequestSwitch);
            startInfo.ArgumentList.Add(directory);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return HostsWriteResult.Fail(HostsWriteOutcome.Failed, "The elevated step did not start.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return Interpret(process.ExitCode, ReadResponse(directory));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return HostsWriteResult.Fail(
                HostsWriteOutcome.Declined,
                "Elevation was declined, so the hosts file was not changed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorFileNotFound)
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, $"Could not start {executable}: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HostsWriteResult.Fail(HostsWriteOutcome.Failed, ex.Message);
        }
        finally
        {
            // The request directory holds a whole copy of the hosts file, so it does not linger.
            // The backup is the durable artefact, not this.
            TryDeleteDirectory(directory);
        }
    }

    private static HostsWriteResult Interpret(int exitCode, HostsWriteResponse? response) => exitCode switch
    {
        HostsWriterExitCodes.Success =>
            new HostsWriteResult(HostsWriteOutcome.Written, null, response?.VerifiedSha256),

        HostsWriterExitCodes.TargetChanged => HostsWriteResult.Fail(
            HostsWriteOutcome.Conflict,
            response?.Message ?? "The hosts file changed while the elevation prompt was open, so nothing was written."),

        HostsWriterExitCodes.Denied => HostsWriteResult.Fail(
            HostsWriteOutcome.Denied,
            Denied(response?.Message)),

        HostsWriterExitCodes.VerifyFailed => HostsWriteResult.Fail(
            HostsWriteOutcome.VerifyFailed,
            response?.Message ?? "The hosts file does not match what was written."),

        HostsWriterExitCodes.PayloadMismatch or HostsWriterExitCodes.MalformedRequest => HostsWriteResult.Fail(
            HostsWriteOutcome.Failed,
            response?.Message ?? "The elevated step rejected the request."),

        _ => HostsWriteResult.Fail(
            HostsWriteOutcome.Failed,
            response?.Message ?? $"The elevated step exited with code {exitCode}."),
    };

    /// <summary>
    /// Access refused <em>after</em> a successful elevation almost never means permissions. It means
    /// something is protecting the file, so say so rather than inviting another futile attempt.
    /// </summary>
    private static string Denied(string? detail) =>
        "Windows refused to write the hosts file even with administrator rights. This is usually "
        + "anti-malware or Controlled Folder Access protecting it rather than a permissions problem"
        + (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}");

    private static string Explain(UnauthorizedAccessException ex) => Denied(ex.Message);

    private static HostsWriteResponse? ReadResponse(string directory)
    {
        try
        {
            var path = Path.Combine(directory, HostsPaths.ResultFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HostsWriteResponse>(File.ReadAllText(path), HostsWriteJson.Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The exit code already told us what happened; this only adds detail.
            return null;
        }
    }

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

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind at worst; it holds no secret the hosts file does not.
        }
    }

    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
