using System;
using System.Diagnostics;
using System.Threading;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Accumulates ingest counters from many parser tasks and forwards throttled
    /// snapshots to an <see cref="IProgress{T}"/>.
    /// <para>
    /// Thread-safe by construction: parsers only ever call the Add* methods, which
    /// are interlocked, and only the throttle decides when a snapshot is published.
    /// Without the throttle a 4-worker ingest would raise a UI render per batch —
    /// tens of thousands of renders on a large search, which costs more than the
    /// parsing.
    /// </para>
    /// </summary>
    internal sealed class LogIngestProgressReporter
    {
        /// <summary>Fast enough to feel live, slow enough not to drown the renderer.</summary>
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

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
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _publishLock = new();

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

        internal LogIngestProgressReporter(IProgress<LogIngestProgress>? sink)
        {
            _sink = sink;
        }

        /// <summary>True when nobody is listening, so callers can skip the bookkeeping.</summary>
        internal bool IsActive => _sink is not null;

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

            _smoothedUnitsPerSecond = 0;
            _lastRateUnits = 0;
            _lastRateAt = _clock.Elapsed;

            Publish(force: true);
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

        /// <summary>
        /// One directory entry looked at during listing. <paramref name="matched"/>
        /// advances the file count so the two figures — examined and kept — can be
        /// shown side by side.
        /// </summary>
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

        /// <summary>Publishes a final snapshot regardless of the throttle.</summary>
        internal void Complete(LogIngestPhase phase)
        {
            _phase = phase;
            Publish(force: true);
        }

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
                    Eta = EstimateRemaining(done, total, now)
                };
            }

            _sink.Report(snapshot);
        }

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
