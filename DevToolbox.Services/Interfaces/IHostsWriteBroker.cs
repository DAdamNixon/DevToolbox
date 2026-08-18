using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Gets bytes onto the hosts file, elevating only if it has to.
/// <para>
/// The application itself runs unelevated. Most developers can be given write access to the hosts
/// file once, after which every switch is a plain in-process write and no prompt ever appears;
/// where that has not been done, a second instance of the application is started with the "runas"
/// verb to do the write and exit. Either way the file is verified afterwards.
/// </para>
/// </summary>
public interface IHostsWriteBroker
{
    /// <summary>Whether the file can be written without elevating. Cheap enough to call on every load.</summary>
    bool CanWriteInProcess(string targetPath);

    /// <summary>
    /// Replaces the hosts file's contents.
    /// </summary>
    /// <param name="expectedOriginalSha256">
    /// The file's hash when it was read. The write is refused if the file no longer matches, so an
    /// edit made while an elevation prompt was on screen is never silently overwritten.
    /// </param>
    /// <param name="restoreFromPath">
    /// A backup to put back if the file ends up not matching what was written. The unelevated path
    /// is a truncate-in-place and so is not atomic; this is what makes that recoverable.
    /// </param>
    Task<HostsWriteResult> WriteAsync(
        string targetPath,
        byte[] content,
        string expectedOriginalSha256,
        string? restoreFromPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one request in an elevated instance of this application and waits for it to finish.
    /// Shared with the permissions service, which uses the same channel to add or remove an
    /// access-control entry.
    /// </summary>
    Task<HostsWriteResult> RunElevatedAsync(
        HostsWriteRequest request,
        byte[]? payload,
        CancellationToken cancellationToken = default);
}
