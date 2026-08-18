using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Keeps copies of the hosts file taken immediately before each change.
/// <para>
/// The backup is what makes the non-atomic write path recoverable, and what makes an unwanted
/// switch undoable. It is taken from the bytes that were actually read, not from a re-read, so it
/// matches the state the change was calculated against.
/// </para>
/// </summary>
public interface IHostsBackupService
{
    /// <summary>Where backups are kept, for the "open folder" action.</summary>
    string BackupDirectory { get; }

    Task<HostsBackup> CreateAsync(byte[] contents, HostsChangeReasonKind reason, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<HostsBackup>> ListAsync(CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(HostsBackup backup, CancellationToken cancellationToken = default);

    Task<HostsBackup?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes all but the newest <paramref name="keep"/>. Zero keeps everything.</summary>
    Task PruneAsync(int keep, CancellationToken cancellationToken = default);
}
