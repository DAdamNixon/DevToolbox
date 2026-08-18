using System.Globalization;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <inheritdoc cref="IHostsBackupService"/>
public sealed class HostsBackupService : IHostsBackupService
{
    /// <summary>
    /// <c>hosts-20260817-143012123-switch.txt</c>. The timestamp and reason live in the name rather
    /// than a sidecar file, so a backup folder stays readable with nothing but Explorer and cannot
    /// end up with metadata that disagrees with the file beside it.
    /// </summary>
    private const string NamePrefix = "hosts-";

    private const string NameSuffix = ".txt";
    private const string DateFormat = "yyyyMMdd";
    private const string TimeFormat = "HHmmssfff";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="backupDirectory">
    /// Where to keep backups. Defaults to the folder beside every other DevToolbox config; the
    /// parameter exists so tests can point somewhere disposable instead of the real one.
    /// </param>
    public HostsBackupService(string? backupDirectory = null)
    {
        BackupDirectory = string.IsNullOrWhiteSpace(backupDirectory) ? HostsPaths.BackupRoot : backupDirectory;
    }

    public string BackupDirectory { get; }

    public async Task<HostsBackup> CreateAsync(
        byte[] contents,
        HostsChangeReasonKind reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(BackupDirectory);

            var takenAt = DateTime.UtcNow;
            var path = Path.Combine(BackupDirectory, BuildName(takenAt, reason));

            await File.WriteAllBytesAsync(path, contents, cancellationToken).ConfigureAwait(false);

            return new HostsBackup(
                Id: Path.GetFileName(path),
                FilePath: path,
                TakenAtUtc: takenAt,
                SizeBytes: contents.Length,
                Reason: reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HostsBackup>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(BackupDirectory)) return (IReadOnlyList<HostsBackup>)Array.Empty<HostsBackup>();

            var backups = new List<HostsBackup>();

            foreach (var path in Directory.EnumerateFiles(BackupDirectory, $"{NamePrefix}*{NameSuffix}"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var backup = TryDescribe(path);
                if (backup is not null) backups.Add(backup);
            }

            return backups.OrderByDescending(backup => backup.TakenAtUtc).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(HostsBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        return await File.ReadAllBytesAsync(backup.FilePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostsBackup?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(backup => string.Equals(backup.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task PruneAsync(int keep, CancellationToken cancellationToken = default)
    {
        if (keep <= 0) return;

        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Count <= keep) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var backup in all.Skip(keep))
            {
                try
                {
                    File.Delete(backup.FilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A backup that will not delete is not worth failing a switch over.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildName(DateTime takenAtUtc, HostsChangeReasonKind reason)
    {
        var local = takenAtUtc.ToLocalTime();

        return string.Concat(
            NamePrefix,
            local.ToString(DateFormat, CultureInfo.InvariantCulture),
            "-",
            local.ToString(TimeFormat, CultureInfo.InvariantCulture),
            "-",
            reason.ToString().ToLowerInvariant(),
            NameSuffix);
    }

    /// <summary>
    /// Reads a backup's timestamp and reason back out of its name. A file that does not match the
    /// pattern is ignored rather than guessed at — the folder is the user's and may hold anything.
    /// </summary>
    private static HostsBackup? TryDescribe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('-');

        if (parts.Length != 4) return null;

        if (!DateTime.TryParseExact(
                parts[1] + parts[2],
                DateFormat + TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                out var takenAtUtc))
        {
            return null;
        }

        if (!Enum.TryParse<HostsChangeReasonKind>(parts[3], ignoreCase: true, out var reason)) return null;

        try
        {
            return new HostsBackup(
                Id: Path.GetFileName(path),
                FilePath: path,
                TakenAtUtc: takenAtUtc,
                SizeBytes: new FileInfo(path).Length,
                Reason: reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
