using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Accumulates ingest counters from many parser tasks and forwards throttled
    /// snapshots to an <see cref="IProgress{T}"/>.
    /// <para>
    /// Thread-safe by construction: parsers only ever call the Add* / File* methods, which
    /// are interlocked or per-file locked, and only the throttle decides when a snapshot is
    /// published. Without the throttle a 4-worker ingest would raise a UI render per batch —
    /// tens of thousands of renders on a large search, which costs more than the
    /// parsing.
    /// </para>
    /// <para>
    /// Also owns the heartbeat: a 1s timer, live only during Scanning and Ingesting, that
    /// forces a publish and re-evaluates every in-flight file's stall state even when nothing
    /// moved. Without it, the last snapshot taken while bytes were still flowing stays on
    /// screen forever once a file stops — the ETA looks wrong when it is really just stale.
    /// </para>
    /// </summary>
    internal sealed class LogIngestProgressReporter : IDisposable
    {
        /// <summary>Fast enough to feel live, slow enough not to drown the renderer.</summary>
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// An estimate is worthless until there is a little evidence behind it. Below
        /// these thresholds the reporter publishes a null Eta and the UI says
        /// "estimating…" instead of showing a number that will immediately change.
        /// </summary>
        private static readonly TimeSpan MinElapsedForEta = TimeSpan.FromSeconds(2);
        private const double MinFractionForEta = 0.01;

        /// <summary>
        /// Smoothing factor for the throughput average. Log files vary enormously in
        /// size and a raw rate makes the estimate jump on every file boundary; this
        /// weights recent throughput without letting one file dominate.
        /// </summary>
        private const double RateSmoothing = 0.3;

        private readonly IProgress<LogIngestProgress>? _sink;
        private readonly LogIngestControl _control;
        private readonly TimeProvider _time;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _publishLock = new();

        private readonly ConcurrentDictionary<string, FileSlot> _files = new(StringComparer.Ordinal);
        private Timer? _heartbeat;

        private long _bytesDone;
        private long _rowsIngested;
        private long _itemsExamined;
        private int _filesDone;
        private string? _currentFile;

        private LogIngestPhase _phase = LogIngestPhase.Scanning;
        private int _filesTotal;
        private long _bytesTotal;

        private TimeSpan _lastPublish = TimeSpan.MinValue;
        private double _smoothedUnitsPerSecond;
        private long _lastRateUnits;
        private TimeSpan _lastRateAt = TimeSpan.Zero;

        internal LogIngestProgressReporter(IProgress<LogIngestProgress>? sink, LogIngestControl? control = null, TimeProvider? time = null)
        {
            _sink = sink;
            _control = control ?? new LogIngestControl();
            _time = time ?? TimeProvider.System;
        }

        /// <summary>True when nobody is listening, so callers can skip the bookkeeping.</summary>
        internal bool IsActive => _sink is not null;

        private sealed class FileSlot
        {
            internal required string FileName;
            internal required string LocationName;
            internal long BytesTotal;
            internal FileIngestState State = FileIngestState.Opening;
            internal string? Reason;
            internal required FileStallTracker Stall;
        }

        /// <summary>
        /// Sets the denominators for the current phase. Pass <paramref name="bytesTotal"/>
        /// as 0 when the phase does not read whole files — the scanning pass only
        /// reads the head of each one, so measuring it in bytes would crawl to 1% and
        /// stop. With no byte total the snapshot falls back to counting files.
        /// </summary>
        internal void SetTotals(int filesTotal, long bytesTotal)
        {
            _filesTotal = filesTotal;
            _bytesTotal = bytesTotal;
            Publish(force: true);
        }

        /// <summary>
        /// Starts a phase and zeroes its progress. Scanning and ingesting cover the
        /// same files at wildly different rates, so carrying either the counters or
        /// the throughput average across the boundary would show a bar that jumps
        /// backwards and a first estimate off by an order of magnitude.
        /// Rows ingested is cumulative and deliberately survives.
        /// </summary>
        internal void EnterPhase(LogIngestPhase phase)
        {
            _phase = phase;

            Interlocked.Exchange(ref _bytesDone, 0);
            Interlocked.Exchange(ref _filesDone, 0);
            Interlocked.Exchange(ref _itemsExamined, 0);
            _currentFile = null;
            _files.Clear();

            _smoothedUnitsPerSecond = 0;
            _lastRateUnits = 0;
            _lastRateAt = _clock.Elapsed;

            if (phase is LogIngestPhase.Scanning or LogIngestPhase.Ingesting)
                StartHeartbeat();
            else
                StopHeartbeat();

            Publish(force: true);
        }

        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatInterval, HeartbeatInterval);
        }

        private void StopHeartbeat()
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
        }

        private void Heartbeat()
        {
            EvaluateStalls();
        }

        /// <summary>
        /// Re-checks every in-flight file against the stall threshold, and auto-skips one that has
        /// exceeded the control's auto-skip timeout. Called by the heartbeat in production; called
        /// directly, against a fake clock, in tests — nothing here waits on real time.
        /// </summary>
        internal void EvaluateStalls()
        {
            var changed = false;

            foreach (var (fileKey, slot) in _files)
            {
                if (slot.State is FileIngestState.Done or FileIngestState.Skipped or FileIngestState.Failed)
                    continue;

                if (!slot.Stall.IsStalled) continue;

                if (slot.State != FileIngestState.Stalled)
                {
                    slot.State = FileIngestState.Stalled;
                    changed = true;
                }

                if (!_control.AutoSkipEnabled) continue;

                if (slot.Stall.SinceLastAdvance >= TimeSpan.FromSeconds(_control.AutoSkipTimeoutSeconds))
                    _control.AutoSkip(fileKey);
            }

            Publish(force: changed);
        }

        internal void AddBytes(long bytes)
        {
            if (bytes > 0) Interlocked.Add(ref _bytesDone, bytes);
            Publish(force: false);
        }

        internal void AddRows(long rows)
        {
            if (rows > 0) Interlocked.Add(ref _rowsIngested, rows);
            Publish(force: false);
        }

        /// <summary>
        /// Names what is being worked on. Forced past the throttle when the name
        /// actually changes: the first thing a slow phase does is announce where it
        /// is, and throttling that away left the label blank for as long as the
        /// first item took to arrive — the exact window where it mattered most.
        /// </summary>
        internal void FileStarted(string fileName)
        {
            var changed = !string.Equals(_currentFile, fileName, StringComparison.Ordinal);
            _currentFile = fileName;
            Publish(force: changed);
        }

        /// <summary>Registers a file about to be opened, in the <see cref="FileIngestState.Opening"/> state.</summary>
        internal void FileOpening(string fileKey, string fileName, string locationName, long bytesTotal)
        {
            _files[fileKey] = new FileSlot
            {
                FileName = fileName,
                LocationName = locationName,
                BytesTotal = bytesTotal,
                Stall = new FileStallTracker(TimeSpan.FromSeconds(_control.Settings.StallThresholdSeconds), _time)
            };
            FileStarted(fileName);
        }

        /// <summary>Records this file's total bytes read so far. Moves Opening to Reading on first call.</summary>
        internal void FileBytesRead(string fileKey, long bytesReadSoFar)
        {
            if (!_files.TryGetValue(fileKey, out var slot)) return;

            var delta = bytesReadSoFar - slot.Stall.BytesRead;
            if (delta > 0)
            {
                if (slot.State is FileIngestState.Opening or FileIngestState.Stalled)
                    slot.State = FileIngestState.Reading;
                slot.Stall.RecordProgress(bytesReadSoFar);
                AddBytes(delta);
            }
        }

        /// <summary>One directory entry looked at during listing.</summary>
        internal void ItemExamined(bool matched)
        {
            Interlocked.Increment(ref _itemsExamined);
            if (matched) Interlocked.Increment(ref _filesDone);
            Publish(force: false);
        }

        internal void FileCompleted()
        {
            Interlocked.Increment(ref _filesDone);
            Publish(force: false);
        }

        /// <summary>A file finished normally; its rows (if any) are in the table.</summary>
        internal void FileDone(string fileKey)
        {
            if (_files.TryGetValue(fileKey, out var slot))
                slot.State = FileIngestState.Done;
            FileCompleted();
        }

        /// <summary>
        /// A file was abandoned — by hand, by auto-skip, or by Cancel. Upserts the slot: a file
        /// skipped before this phase ever opened it (control already said so from an earlier phase)
        /// has none yet.
        /// </summary>
        internal void FileSkipped(string fileKey, string fileName, string locationName, string reason)
        {
            var slot = Upsert(fileKey, fileName, locationName);
            slot.State = FileIngestState.Skipped;
            slot.Reason = reason;
            FileCompleted();
        }

        /// <summary>A file could not be opened or read; the load continues without it (D5).</summary>
        internal void FileFailed(string fileKey, string fileName, string locationName, string reason)
        {
            var slot = Upsert(fileKey, fileName, locationName);
            slot.State = FileIngestState.Failed;
            slot.Reason = reason;
            FileCompleted();
        }

        private FileSlot Upsert(string fileKey, string fileName, string locationName) =>
            _files.GetOrAdd(fileKey, _ => new FileSlot
            {
                FileName = fileName,
                LocationName = locationName,
                Stall = new FileStallTracker(TimeSpan.FromSeconds(_control.Settings.StallThresholdSeconds), _time)
            });

        /// <summary>Every file that ended this phase Skipped or Failed, for the partial-result banner.</summary>
        internal IReadOnlyList<NotIngestedFile> GetNotIngested() =>
            _files.Values
                .Where(slot => slot.State is FileIngestState.Skipped or FileIngestState.Failed)
                .Select(slot => new NotIngestedFile
                {
                    FileName = slot.FileName,
                    LocationName = slot.LocationName,
                    State = slot.State,
                    BytesRead = slot.Stall.BytesRead,
                    BytesTotal = slot.BytesTotal,
                    Reason = slot.Reason ?? "not read"
                })
                .ToList();

        /// <summary>Publishes a final snapshot regardless of the throttle.</summary>
        internal void Complete(LogIngestPhase phase)
        {
            _phase = phase;
            StopHeartbeat();
            Publish(force: true);
        }

        public void Dispose() => StopHeartbeat();

        private void Publish(bool force)
        {
            if (_sink is null) return;

            LogIngestProgress snapshot;
            lock (_publishLock)
            {
                var now = _clock.Elapsed;
                if (!force && now - _lastPublish < MinInterval) return;
                _lastPublish = now;

                var bytesDone = Interlocked.Read(ref _bytesDone);
                var filesDone = Volatile.Read(ref _filesDone);

                // Estimate against whichever denominator this phase actually has.
                // Ingest knows its byte total and is measured that way; the scanning
                // pass only reads file heads, so bytes mean nothing there and files
                // are the honest unit. Without this, scanning — which over a slow
                // share is the longest phase — said "estimating…" from start to end.
                var (done, total) = _bytesTotal > 0 ? (bytesDone, _bytesTotal) : (filesDone, (long)_filesTotal);

                var inFlight = _files
                    .Where(kv => kv.Value.State is FileIngestState.Opening or FileIngestState.Reading or FileIngestState.Stalled)
                    .Select(kv => new FileProgressSnapshot
                    {
                        FileKey = kv.Key,
                        FileName = kv.Value.FileName,
                        LocationName = kv.Value.LocationName,
                        State = kv.Value.State,
                        BytesRead = kv.Value.Stall.BytesRead,
                        BytesTotal = kv.Value.BytesTotal,
                        SinceLastAdvance = kv.Value.Stall.SinceLastAdvance
                    })
                    .ToList();

                var stalled = inFlight.Count(f => f.State == FileIngestState.Stalled);
                var skipped = _files.Values.Count(f => f.State == FileIngestState.Skipped);
                var failed = _files.Values.Count(f => f.State == FileIngestState.Failed);

                snapshot = new LogIngestProgress
                {
                    Phase = _phase,
                    FilesTotal = _filesTotal,
                    FilesDone = filesDone,
                    BytesTotal = _bytesTotal,
                    BytesDone = bytesDone,
                    RowsIngested = Interlocked.Read(ref _rowsIngested),
                    ItemsExamined = Interlocked.Read(ref _itemsExamined),
                    CurrentFile = _currentFile,
                    Elapsed = now,
                    Eta = HasStalledInFlight(inFlight, stalled) ? null : EstimateRemaining(done, total, now),
                    InFlight = inFlight,
                    FilesStalled = stalled,
                    FilesSkipped = skipped,
                    FilesFailed = failed
                };
            }

            _sink.Report(snapshot);
        }

        /// <summary>
        /// True when every file still open is stalled — the moment an estimate stops meaning
        /// anything, because nothing is moving to measure a rate from.
        /// </summary>
        private static bool HasStalledInFlight(List<FileProgressSnapshot> inFlight, int stalledCount) =>
            inFlight.Count > 0 && stalledCount == inFlight.Count;

        /// <summary>
        /// Time remaining, from the rate at which <paramref name="done"/> is
        /// approaching <paramref name="total"/>. The unit is whatever the caller
        /// chose — bytes for ingest, files for scanning — since the arithmetic is
        /// the same either way.
        /// Caller must hold <see cref="_publishLock"/>.
        /// </summary>
        private TimeSpan? EstimateRemaining(long done, long total, TimeSpan now)
        {
            if (total <= 0 || done <= 0) return null;
            if (now < MinElapsedForEta) return null;
            if ((double)done / total < MinFractionForEta) return null;

            var window = now - _lastRateAt;
            if (window > TimeSpan.Zero)
            {
                var instantRate = (done - _lastRateUnits) / window.TotalSeconds;
                _smoothedUnitsPerSecond = _smoothedUnitsPerSecond <= 0
                    ? instantRate
                    : (RateSmoothing * instantRate) + ((1 - RateSmoothing) * _smoothedUnitsPerSecond);

                _lastRateUnits = done;
                _lastRateAt = now;
            }

            if (_smoothedUnitsPerSecond <= 0) return null;

            var remaining = total - done;
            if (remaining <= 0) return TimeSpan.Zero;

            var seconds = remaining / _smoothedUnitsPerSecond;

            // A wild estimate is worse than none: past a day it is certainly an
            // artefact of a stalled share rather than real work remaining.
            return seconds > TimeSpan.FromDays(1).TotalSeconds
                ? null
                : TimeSpan.FromSeconds(seconds);
        }
    }
}
