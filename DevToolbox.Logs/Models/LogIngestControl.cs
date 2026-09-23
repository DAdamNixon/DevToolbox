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

        /// <summary>Abandons one file, named by <see cref="FileProgressSnapshot.FileKey"/>.</summary>
        public void Skip(string fileKey) => Resolve(fileKey, SkipReason.Manual);

        /// <summary>Abandons every file still in flight — what the Cancel button does.</summary>
        public void SkipAll()
        {
            _allSkipped = true;
            foreach (var fileKey in _signals.Keys)
                Resolve(fileKey, SkipReason.Cancelled);
        }

        internal void AutoSkip(string fileKey) => Resolve(fileKey, SkipReason.Auto);

        /// <summary>True once this file has been asked to stop, for any reason.</summary>
        internal bool IsSkipped(string fileKey) =>
            _allSkipped || (_signals.TryGetValue(fileKey, out var tcs) && tcs.Task.IsCompleted);

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
