namespace DevToolbox.Services.Models.Hosts;

/// <summary>The hosts file as of one read: its lines, what they mean, and whether we can write it.</summary>
public sealed record HostsSnapshot(
    HostsDocument Document,
    HostsMap Map,
    DateTime ReadAtUtc,
    bool IsWritableInProcess);

/// <summary>Why a change was made. Recorded in the backup's file name.</summary>
public enum HostsChangeReasonKind
{
    /// <summary>Switching a group to one of its options, or turning the group off.</summary>
    Switch,

    /// <summary>Inserting a scope-reset directive to close an unterminated group.</summary>
    InsertClear,

    /// <summary>Saving the raw editor.</summary>
    RawEdit,

    /// <summary>Restoring a backup.</summary>
    Restore,

    /// <summary>Adding a new group and its options.</summary>
    AddGroup,

    /// <summary>Adding an option to an existing group.</summary>
    AddOption,

    /// <summary>Adding entries to an existing option.</summary>
    AddEntry,

    /// <summary>Renaming or rewriting something that already existed.</summary>
    Edit,

    /// <summary>Removing a group or an option.</summary>
    Delete,
}

/// <summary>A copy of the hosts file taken immediately before a change.</summary>
/// <param name="Id">The backup's file name, which is also how the UI refers to it.</param>
public sealed record HostsBackup(
    string Id,
    string FilePath,
    DateTime TakenAtUtc,
    long SizeBytes,
    HostsChangeReasonKind Reason);

/// <summary>How a write ended.</summary>
public enum HostsWriteOutcome
{
    /// <summary>Written directly, no elevation needed.</summary>
    Written,

    /// <summary>Written by an elevated helper after one prompt.</summary>
    WrittenElevated,

    /// <summary>The user declined the elevation prompt. Nothing was changed.</summary>
    Declined,

    /// <summary>The file changed on disk between reading it and writing it. Nothing was changed.</summary>
    Conflict,

    /// <summary>The write happened but the file does not match what was written. Restored from backup.</summary>
    VerifyFailed,

    /// <summary>Access was refused even after elevation — usually anti-malware, not permissions.</summary>
    Denied,

    Failed,
}

public sealed record HostsWriteResult(HostsWriteOutcome Outcome, string? Error, string? WrittenSha256)
{
    public bool Success => Outcome is HostsWriteOutcome.Written or HostsWriteOutcome.WrittenElevated;

    public static HostsWriteResult Fail(HostsWriteOutcome outcome, string error) => new(outcome, error, null);
}

/// <summary>How a change should be carried out.</summary>
/// <param name="IncludeSuspectLines">
/// Sweep in quarantined lines. Only ever true because a developer looked at the diff and agreed.
/// </param>
public sealed record HostsApplyOptions(
    bool IncludeSuspectLines = false,
    bool TakeBackup = true,
    bool RunAfterApply = true);

public enum HostsApplyStatus
{
    Applied,

    /// <summary>The file was already in the requested state.</summary>
    NoChange,

    /// <summary>The change would touch quarantined lines and was not confirmed.</summary>
    BlockedByAnomaly,

    ElevationDeclined,

    /// <summary>Someone else edited the file first.</summary>
    Conflict,

    Failed,
}

/// <summary>
/// The outcome of a change, following the same shape as <see cref="OpenResult"/> so the UI can
/// surface a failure in a banner rather than losing it.
/// </summary>
public sealed record HostsApplyResult(
    HostsApplyStatus Status,
    string? Error,
    HostsSnapshot? Snapshot,
    HostsBackup? Backup,
    IReadOnlyList<HostsLineChange> Changes,
    IReadOnlyList<HostsAnomaly> Blocking,
    string? AfterApplyMessage)
{
    public bool Success => Status is HostsApplyStatus.Applied or HostsApplyStatus.NoChange;

    public static HostsApplyResult Fail(HostsApplyStatus status, string error, HostsSnapshot? snapshot = null) =>
        new(status, error, snapshot, null, [], [], null);

    public static HostsApplyResult Blocked(HostsSnapshot snapshot, IReadOnlyList<HostsAnomaly> blocking) =>
        new(HostsApplyStatus.BlockedByAnomaly,
            "This change would touch lines that do not appear to belong to the option.",
            snapshot,
            null,
            [],
            blocking,
            null);
}

/// <summary>Raised when the hosts file changes, whoever changed it.</summary>
public sealed class HostsSnapshotChangedEventArgs(HostsSnapshot snapshot, bool causedByUs) : EventArgs
{
    public HostsSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// True when this application wrote the change. Lets the tray skip redrawing for its own work
    /// and only react to somebody else's edit.
    /// </summary>
    public bool CausedByUs { get; } = causedByUs;
}
