using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <inheritdoc cref="IHostsFileService"/>
public sealed class HostsFileService : IHostsFileService
{
    private readonly IHostsSettingsService _settings;
    private readonly IHostsBackupService _backups;
    private readonly IHostsWriteBroker _broker;
    private readonly ISystemService _system;
    private readonly IOpenHandlerService _openHandlers;

    /// <summary>Serialises changes, so a second switch cannot start while a prompt is on screen.</summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private readonly CancellationTokenSource _lifetime = new();

    private FileSystemWatcher? _watcher;
    private Timer? _watcherDebounce;

    /// <summary>
    /// Holds a reference to the polling loop for the lifetime of this service. Not awaited — it ends
    /// when <see cref="_lifetime"/> is cancelled — but a running loop should be rooted rather than
    /// left to chance.
    /// </summary>
    private Task? _pollLoop;
    private bool _initialised;
    private bool _disposed;

    /// <summary>
    /// The hash we most recently wrote. An external-change notification matching it is our own echo,
    /// so subscribers are told as much and the tray does not flicker on its own work.
    /// </summary>
    private string? _lastWrittenSha256;

    public HostsFileService(
        IHostsSettingsService settings,
        IHostsBackupService backups,
        IHostsWriteBroker broker,
        ISystemService system,
        IOpenHandlerService openHandlers)
    {
        _settings = settings;
        _backups = backups;
        _broker = broker;
        _system = system;
        _openHandlers = openHandlers;
    }

    public string HostsPath { get; private set; } = HostsSettings.DefaultHostsPath();

    public HostsSnapshot? Current { get; private set; }

    public string? LoadError { get; private set; }

    public bool IsApplying { get; private set; }

    public event EventHandler<HostsSnapshotChangedEventArgs>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialised) return;
        _initialised = true;

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        StartWatching();

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        _pollLoop = PollAsync(Math.Max(1, settings.RefreshSeconds), _lifetime.Token);
    }

    public async Task<HostsSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        HostsPath = settings.ResolveHostsPath();

        var snapshot = await Task.Run(() =>
        {
            var document = HostsDocumentCodec.Read(HostsPath);
            var map = HostsAnnotationParser.Parse(document, settings.ToDialect());

            return new HostsSnapshot(document, map, DateTime.UtcNow, _broker.CanWriteInProcess(HostsPath));
        }, cancellationToken).ConfigureAwait(false);

        var changed = Current is null ||
                      !string.Equals(Current.Document.Sha256, snapshot.Document.Sha256, StringComparison.OrdinalIgnoreCase);

        Current = snapshot;
        LoadError = null;

        if (changed)
        {
            var causedByUs = string.Equals(snapshot.Document.Sha256, _lastWrittenSha256, StringComparison.OrdinalIgnoreCase);
            Changed?.Invoke(this, new HostsSnapshotChangedEventArgs(snapshot, causedByUs));
        }

        return snapshot;
    }

    public HostsChangePreview Preview(HostsSnapshot snapshot, string group, string? option, bool includeSuspectLines = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var mutation = HostsMutator.SetOption(snapshot.Document, snapshot.Map, group, option, includeSuspectLines);

        return new HostsChangePreview(
            HostsChangeReasonKind.Switch,
            group,
            option,
            mutation.Document,
            mutation.Changes,
            includeSuspectLines ? [] : BlockingFor(snapshot, group, option),
            includeSuspectLines);
    }

    /// <summary>
    /// Anomalies that stand in the way of this particular change — only the ones affecting the
    /// group being switched, so a problem elsewhere in the file does not block unrelated work.
    /// </summary>
    private static IReadOnlyList<HostsAnomaly> BlockingFor(HostsSnapshot snapshot, string group, string? option) =>
        snapshot.Map.BlockingAnomalies
            .Where(anomaly => string.Equals(anomaly.Group, group, StringComparison.Ordinal))
            .ToArray();

    public Task<HostsApplyResult> ApplyAsync(
        string group,
        string? option,
        HostsApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(options);

        return ChangeAsync(
            HostsChangeReasonKind.Switch,
            options,
            (snapshot, settings) =>
            {
                var sweep = options.IncludeSuspectLines;

                var blocking = sweep ? [] : BlockingFor(snapshot, group, option);
                if (blocking.Count > 0 && settings.BlockApplyOnSuspectLines)
                {
                    return (null, blocking);
                }

                var mutation = HostsMutator.SetOption(snapshot.Document, snapshot.Map, group, option, sweep);
                return (mutation, []);
            },
            cancellationToken);
    }

    public Task<HostsApplyResult> InsertClearAsync(int beforeLine, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            HostsChangeReasonKind.InsertClear,
            new HostsApplyOptions(RunAfterApply: false),
            (snapshot, _) => (HostsMutator.InsertClear(snapshot.Document, snapshot.Map.Dialect, beforeLine), []),
            cancellationToken);

    public HostsChangePreview PreviewAddition(HostsSnapshot snapshot, HostsAddition addition)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(addition);

        var mutation = HostsMutator.Add(snapshot.Document, snapshot.Map, addition);

        return new HostsChangePreview(
            addition.Reason,
            addition.TargetGroup,
            addition.TargetOption,
            mutation.Document,
            mutation.Changes,
            [],
            SuspectLinesIncluded: false);
    }

    public Task<HostsApplyResult> AddAsync(HostsAddition addition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addition);

        return ChangeAsync(
            addition.Reason,

            // A new group or option is written switched off, so nothing it names is being resolved
            // yet and there is no stale cache to clear. Entries added to an option that is already
            // on do change what resolves, so those get the flush.
            new HostsApplyOptions(RunAfterApply: addition is HostsAddition.Entries),
            (snapshot, _) => (HostsMutator.Add(snapshot.Document, snapshot.Map, addition), []),
            cancellationToken);
    }

    public HostsChangePreview PreviewEdit(HostsSnapshot snapshot, HostsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(edit);

        var mutation = HostsEditor.Apply(snapshot.Document, snapshot.Map, edit);

        return new HostsChangePreview(
            edit.Reason,
            edit.TargetGroup,
            edit.TargetOption,
            mutation.Document,
            mutation.Changes,
            [],
            SuspectLinesIncluded: false);
    }

    public Task<HostsApplyResult> EditAsync(HostsEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return ChangeAsync(
            edit.Reason,

            // An edit can change or remove a live entry, so the DNS cache may well be stale
            // afterwards even though no option was switched.
            new HostsApplyOptions(),
            (snapshot, _) => (HostsEditor.Apply(snapshot.Document, snapshot.Map, edit), []),
            cancellationToken);
    }

    public Task<HostsApplyResult> SaveRawAsync(
        string text,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        return ChangeAsync(
            HostsChangeReasonKind.RawEdit,
            new HostsApplyOptions(),
            (snapshot, _) =>
            {
                if (!string.Equals(snapshot.Document.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HostsConflictException(
                        "The hosts file changed since the editor loaded it. Reload before saving.");
                }

                // A raw save is not a comment-marker change, so it deliberately does not go through
                // the invariant checker — the developer typed exactly what they meant. The backup and
                // the hash verification after the write still apply.
                var document = HostsDocumentCodec.FromText(snapshot.Document, text);
                return (new HostsMutation(document, []), []);
            },
            cancellationToken);
    }

    public async Task<HostsApplyResult> RestoreBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var backup = await _backups.FindAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return HostsApplyResult.Fail(HostsApplyStatus.Failed, $"Backup '{backupId}' no longer exists.", Current);
        }

        var contents = await _backups.ReadAsync(backup, cancellationToken).ConfigureAwait(false);

        return await ChangeAsync(
            HostsChangeReasonKind.Restore,
            new HostsApplyOptions(),
            (snapshot, _) =>
            {
                var document = HostsDocumentCodec.FromBytes(
                    snapshot.Document.Path, contents, snapshot.Document.LastWriteTimeUtc);

                // Carried over so the write's precondition still describes the file on disk rather
                // than the backup it came from.
                return (new HostsMutation(WithPrecondition(document, snapshot.Document), []), []);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpenResult> OpenHostsFileAsync()
    {
        var settings = await _settings.GetAsync().ConfigureAwait(false);

        // Named editor first, then whatever openHandlers.yaml says, then the shell — the same
        // precedence the dashboard's Open button uses, so one config decides for both.
        if (settings.Editor is not null)
        {
            return await _system.OpenWithCustomAppAsync(HostsPath, settings.Editor).ConfigureAwait(false);
        }

        // HandlerFor reads a cached config, so the config has to be loaded once first.
        await _openHandlers.GetConfigAsync().ConfigureAwait(false);
        var handler = _openHandlers.HandlerFor(HostsPath);

        return handler is not null
            ? await _system.OpenWithCustomAppAsync(HostsPath, handler).ConfigureAwait(false)
            : await _system.OpenLocationAsync(HostsPath).ConfigureAwait(false);
    }

    public Task<OpenResult> OpenHostsFolderAsync() =>
        _system.OpenInExplorerAsync(Path.GetDirectoryName(HostsPath) ?? HostsPath);

    // ── the one path every change takes ──────────────────────────────────────

    /// <summary>
    /// Backup, verify, write, verify again, reload, then run the after-apply command.
    /// <para>
    /// Every kind of change funnels through here so none of them can skip a step. The mutation is
    /// produced by the caller's callback; everything protective around it is the same each time.
    /// </para>
    /// </summary>
    private async Task<HostsApplyResult> ChangeAsync(
        HostsChangeReasonKind reason,
        HostsApplyOptions options,
        Func<HostsSnapshot, HostsSettings, (HostsMutation? Mutation, IReadOnlyList<HostsAnomaly> Blocking)> plan,
        CancellationToken cancellationToken)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsApplying = true;

        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

            // Always work from a fresh read: the snapshot the UI is showing may be seconds old.
            var snapshot = await RefreshAsync(cancellationToken).ConfigureAwait(false);

            HostsMutation? mutation;
            IReadOnlyList<HostsAnomaly> blocking;

            try
            {
                (mutation, blocking) = plan(snapshot, settings);
            }
            catch (HostsConflictException ex)
            {
                return HostsApplyResult.Fail(HostsApplyStatus.Conflict, ex.Message, snapshot);
            }
            catch (KeyNotFoundException ex)
            {
                return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, snapshot);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, snapshot);
            }

            // Authoring rejects what it was given: a name carrying the dialect's punctuation, an
            // address that is not one, an option name already taken. These carry a message written
            // for the person who typed it, so they are surfaced rather than swallowed.
            catch (ArgumentException ex)
            {
                return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, snapshot);
            }
            catch (InvalidOperationException ex)
            {
                return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, snapshot);
            }

            if (mutation is null) return HostsApplyResult.Blocked(snapshot, blocking);

            var content = HostsDocumentCodec.Compose(mutation.Document);

            if (string.Equals(HostsDocument.HashOf(content), snapshot.Document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new HostsApplyResult(HostsApplyStatus.NoChange, null, snapshot, null, [], [], null);
            }

            // A switch only ever moves comment markers, so it is proved to have done that and
            // nothing else. A raw edit has no such promise to keep and carries no change list.
            if (mutation.Changes.Count > 0)
            {
                try
                {
                    HostsInvariantChecker.Verify(snapshot.Document, mutation.Document, mutation.Changes);
                }
                catch (InvalidOperationException ex)
                {
                    return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, snapshot);
                }
            }

            HostsBackup? backup = null;
            if (options.TakeBackup)
            {
                backup = await _backups
                    .CreateAsync(HostsDocumentCodec.Compose(snapshot.Document), reason, cancellationToken)
                    .ConfigureAwait(false);
            }

            var write = await _broker.WriteAsync(
                HostsPath, content, snapshot.Document.Sha256, backup?.FilePath, cancellationToken).ConfigureAwait(false);

            if (!write.Success)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return new HostsApplyResult(StatusFor(write.Outcome), write.Error, Current, backup, [], [], null);
            }

            _lastWrittenSha256 = write.WrittenSha256 ?? HostsDocument.HashOf(content);

            await _backups.PruneAsync(settings.BackupRetention, cancellationToken).ConfigureAwait(false);

            var reloaded = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            var afterApply = options.RunAfterApply
                ? await RunAfterApplyAsync(settings, cancellationToken).ConfigureAwait(false)
                : null;

            return new HostsApplyResult(
                HostsApplyStatus.Applied, null, reloaded, backup, mutation.Changes, [], afterApply);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HostsApplyResult.Fail(HostsApplyStatus.Failed, ex.Message, Current);
        }
        finally
        {
            IsApplying = false;
            _applyGate.Release();
        }
    }

    private static HostsApplyStatus StatusFor(HostsWriteOutcome outcome) => outcome switch
    {
        HostsWriteOutcome.Declined => HostsApplyStatus.ElevationDeclined,
        HostsWriteOutcome.Conflict => HostsApplyStatus.Conflict,
        _ => HostsApplyStatus.Failed,
    };

    /// <summary>
    /// Runs the configured after-apply command, in this process and unelevated.
    /// <para>
    /// Never fails the change: a DNS cache that did not flush is worth reporting, not worth undoing
    /// a switch over. And it stays out of the elevated step deliberately — this command comes from
    /// user-editable config, and config must not be able to name something that runs as
    /// administrator.
    /// </para>
    /// </summary>
    private async Task<string?> RunAfterApplyAsync(HostsSettings settings, CancellationToken cancellationToken)
    {
        if (settings.AfterApply is null) return null;

        var result = await _system.RunToCompletionAsync(settings.AfterApply, cancellationToken).ConfigureAwait(false);

        return result.Success ? null : $"{settings.AfterApply.Name}: {result.Describe()}";
    }

    /// <summary>
    /// Gives a document read from somewhere else the identity of the file on disk, so the write's
    /// precondition still describes the target rather than where the bytes came from.
    /// </summary>
    private static HostsDocument WithPrecondition(HostsDocument document, HostsDocument target) =>
        new()
        {
            Path = target.Path,
            Encoding = document.Encoding,
            Preamble = document.Preamble,
            DefaultNewLine = document.DefaultNewLine,
            Lines = document.Lines,
            Sha256 = target.Sha256,
            LastWriteTimeUtc = target.LastWriteTimeUtc,
            Length = target.Length,
            DecodedWithFallbackEncoding = document.DecodedWithFallbackEncoding,
        };

    // ── noticing somebody else's edit ────────────────────────────────────────

    /// <summary>
    /// A cheap timestamp-and-length comparison on a three-kilobyte file. Primary rather than the
    /// watcher, because a poll always works: the watcher can be refused, can overflow its buffer,
    /// and can miss a save that replaces the file rather than editing it.
    /// </summary>
    private async Task PollAsync(int seconds, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshIfChangedOnDiskAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RefreshIfChangedOnDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var known = Current;
            if (known is null)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // Qualified: DevToolbox.Services.Models has a FileInfo of its own.
            var info = new System.IO.FileInfo(HostsPath);
            if (!info.Exists) return;

            if (info.Length == known.Document.Length && info.LastWriteTimeUtc == known.Document.LastWriteTimeUtc)
            {
                return;
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
        }
    }

    /// <summary>
    /// Watches the directory rather than the file, and handles creation and renaming as well as
    /// writing, because most editors save a file by replacing it. Debounced, because a single save
    /// raises several events.
    /// </summary>
    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(HostsPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        try
        {
            _watcherDebounce = new Timer(_ => _ = RefreshIfChangedOnDiskAsync(_lifetime.Token));

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(HostsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };

            _watcher.Changed += OnWatcherSignal;
            _watcher.Created += OnWatcherSignal;
            _watcher.Renamed += OnWatcherSignal;
            _watcher.Deleted += OnWatcherSignal;
            _watcher.Error += (_, _) => Nudge();

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or UnauthorizedAccessException)
        {
            // A watcher that cannot be created is an optimisation lost, not a failure: the poll loop
            // covers the same ground a few seconds later.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnWatcherSignal(object sender, FileSystemEventArgs e) => Nudge();

    private const int WatcherDebounceMilliseconds = 400;

    private void Nudge() =>
        _watcherDebounce?.Change(WatcherDebounceMilliseconds, Timeout.Infinite);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lifetime.Cancel();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatcherSignal;
            _watcher.Created -= OnWatcherSignal;
            _watcher.Renamed -= OnWatcherSignal;
            _watcher.Deleted -= OnWatcherSignal;
            _watcher.Dispose();
        }

        _watcherDebounce?.Dispose();
        _lifetime.Dispose();
        _applyGate.Dispose();
    }
}

/// <summary>The file moved under us. Distinct from a failure, because the remedy is to reload.</summary>
public sealed class HostsConflictException(string message) : Exception(message);
