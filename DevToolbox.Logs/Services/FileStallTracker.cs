using System;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// One file's liveness: has it advanced recently, against a <see cref="TimeProvider"/> so a test
    /// can move the clock instead of waiting on it. Liveness is bytes advancing (D1) — never a fixed
    /// duration — so a slow file that keeps moving is never mistaken for a stalled one.
    /// </summary>
    internal sealed class FileStallTracker
    {
        private readonly TimeSpan _stallThreshold;
        private readonly TimeProvider _time;
        private readonly object _gate = new();

        private long _bytesRead;
        private DateTimeOffset _lastAdvanceAt;

        internal FileStallTracker(TimeSpan stallThreshold, TimeProvider time)
        {
            _stallThreshold = stallThreshold;
            _time = time;
            _lastAdvanceAt = time.GetUtcNow();
        }

        internal long BytesRead
        {
            get { lock (_gate) return _bytesRead; }
        }

        internal TimeSpan SinceLastAdvance
        {
            get { lock (_gate) return _time.GetUtcNow() - _lastAdvanceAt; }
        }

        internal bool IsStalled => SinceLastAdvance >= _stallThreshold;

        /// <summary>Records the file's total bytes read so far. Ignores a value that is not new progress.</summary>
        internal void RecordProgress(long bytesReadSoFar)
        {
            lock (_gate)
            {
                if (bytesReadSoFar <= _bytesRead) return;
                _bytesRead = bytesReadSoFar;
                _lastAdvanceAt = _time.GetUtcNow();
            }
        }
    }
}
