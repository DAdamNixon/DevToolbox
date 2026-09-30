using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DevToolbox.Services.Models
{
    /// <summary>Why a file stopped being read, for the reason text on a partial result.</summary>
    public enum SkipReason
    {
        /// <summary>Someone clicked Skip.</summary>
        Manual,

        /// <summary>The configured stall timeout elapsed with auto-skip on.</summary>
        Auto,

        /// <summary>The whole search was cancelled.</summary>
        Cancelled
    }

    /// <summary>
    /// The caller's handle onto one running <c>PrepareLogTableAsync</c>: its settings, and the
    /// signal that lets it skip one file — or all of them, which is what Cancel becomes.
    /// <para>
    /// <see cref="Skip"/> and <see cref="SkipAll"/> only ever set a flag; they do not touch the file
    /// or its thread. The ingest loop is what notices and abandons the read — see
    /// <c>DbLogService</c>. That split is deliberate: a file opened synchronously cannot be made to
    /// stop from outside, so this object promises only "stop waiting on it", never "stop it".
    /// </para>
    /// </summary>
    public sealed class LogIngestControl
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SkipReason>> _signals =
            new(StringComparer.Ordinal);

        private volatile bool _allSkipped;

        /// <param name="settings">Thresholds and the UI's own auto-skip toggle. Defaults applied when omitted.</param>
        /// <param name="alwaysAutoSkip">
        /// True for a caller with nobody watching to click Skip (the MCP server, D6) — auto-skip
        /// applies at <see cref="LogIngestSettings.McpAutoSkipTimeoutSeconds"/> regardless of
        /// <see cref="LogIngestSettings.UiAutoSkipEnabled"/>.
        /// </param>
        public LogIngestControl(LogIngestSettings? settings = null, bool alwaysAutoSkip = false)
        {
            Settings = settings ?? new LogIngestSettings();
            AlwaysAutoSkip = alwaysAutoSkip;
        }

        public LogIngestSettings Settings { get; }

        internal bool AlwaysAutoSkip { get; }

        internal bool AutoSkipEnabled => AlwaysAutoSkip || Settings.UiAutoSkipEnabled;

        internal int AutoSkipTimeoutSeconds => AlwaysAutoSkip
            ? Settings.McpAutoSkipTimeoutSeconds
            : Settings.UiAutoSkipTimeoutSeconds;

        /// <summary>
        /// Abandons one file, named by <see cref="FileProgressSnapshot.FileKey"/>. Too late for a file
        /// that has already finished reading: the drawer's list trails the ingest by up to a second, and
        /// a Skip aimed at a stalled file that recovered and finished meanwhile must not purge a complete
        /// file while it goes on being reported as done.
        /// </summary>
        public void Skip(string fileKey)
        {
            lock (_finishLock)
            {
                if (_finished.Contains(fileKey)) return;
                Resolve(fileKey, SkipReason.Manual);
            }
        }

        /// <summary>
        /// Abandons every file still in flight — half of what the Cancel button does; the other
        /// half is cancelling the prepare's token. Rows these files already committed stay in the
        /// table (see <see cref="ShouldPurge"/>); their files are reported as cut short.
        /// </summary>
        public void SkipAll()
        {
            _allSkipped = true;
            foreach (var fileKey in _signals.Keys)
                Resolve(fileKey, SkipReason.Cancelled);
        }

        internal void AutoSkip(string fileKey)
        {
            lock (_finishLock)
            {
                if (_finished.Contains(fileKey)) return;
                Resolve(fileKey, SkipReason.Auto);
            }
        }

        private readonly object _finishLock = new();
        private readonly HashSet<string> _finished = new(StringComparer.Ordinal);

        /// <summary>
        /// Seals a file as having read to the end, unless a skip got there first — in which case it
        /// returns false and the file is a skipped one. Kept apart from the skip signal on purpose: that
        /// signal is what an in-flight read races against, and resolving it here would make a file that
        /// finished look abandoned.
        /// </summary>
        internal bool TryMarkFinished(string fileKey)
        {
            lock (_finishLock)
            {
                if (IsSkipped(fileKey)) return false;
                _finished.Add(fileKey);
                return true;
            }
        }

        /// <summary>The file read to its end before anything skipped it.</summary>
        internal bool IsFinished(string fileKey)
        {
            lock (_finishLock) return _finished.Contains(fileKey);
        }

        /// <summary><see cref="SkipAll"/> has been called.</summary>
        internal bool AllSkipped => _allSkipped;

        /// <summary>True once this file has been asked to stop, for any reason.</summary>
        internal bool IsSkipped(string fileKey) =>
            _allSkipped || (_signals.TryGetValue(fileKey, out var tcs) && tcs.Task.IsCompleted);

        /// <summary>
        /// True when this file was skipped on its own — by hand or by auto-skip — and so must
        /// contribute zero rows (D4). False for a file stopped only by <see cref="SkipAll"/>: a
        /// cancelled load keeps what it had already read, because those rows may already be on
        /// screen and are exactly what someone pressing Cancel mid-load wanted to look at.
        /// <para>
        /// Decided by the reason, never by whether a token happens to be cancelled yet — the
        /// writer used to avoid purging a cancelled load only because its next storage call threw.
        /// The first reason recorded wins, so Skip then Cancel still purges that one file.
        /// </para>
        /// </summary>
        internal bool ShouldPurge(string fileKey) =>
            _signals.TryGetValue(fileKey, out var tcs) &&
            tcs.Task.IsCompletedSuccessfully &&
            tcs.Task.Result is SkipReason.Manual or SkipReason.Auto;

        private volatile bool _tableDropped;
        private volatile string? _readyTable;

        /// <summary>
        /// This prepare has dropped the table it is about to rebuild. Anything a caller was showing
        /// from that table is gone from under it, whatever happens next.
        /// </summary>
        public bool TableDropped => _tableDropped;

        /// <summary>
        /// The table this prepare created and is now filling, from the moment it exists; null before.
        /// <para>
        /// The signal a live view waits for. Until it is set, a table by this name is the previous
        /// search's — or, between the drop and the create, nothing at all — so querying it would
        /// show someone else's rows as this load's. Set synchronously by the ingest rather than
        /// inferred from progress, which is throttled and can arrive after the prepare has already
        /// finished or failed.
        /// </para>
        /// </summary>
        public string? ReadyTable => _readyTable;

        internal void MarkTableDropped() => _tableDropped = true;

        internal void MarkTableReady(string tableName) => _readyTable = tableName;

        /// <summary>
        /// Resolves the instant this file is asked to stop — no polling. Safe to await even for a
        /// file already finished normally; nothing calls it back in that case and the ingest loop
        /// simply wins the race.
        /// </summary>
        internal Task<SkipReason> WaitForAbandonAsync(string fileKey)
        {
            var tcs = _signals.GetOrAdd(fileKey, _ => new TaskCompletionSource<SkipReason>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_allSkipped)
                tcs.TrySetResult(SkipReason.Cancelled);
            return tcs.Task;
        }

        private void Resolve(string fileKey, SkipReason reason)
        {
            var tcs = _signals.GetOrAdd(fileKey, _ => new TaskCompletionSource<SkipReason>(TaskCreationOptions.RunContinuationsAsynchronously));
            tcs.TrySetResult(reason);
        }
    }
}
