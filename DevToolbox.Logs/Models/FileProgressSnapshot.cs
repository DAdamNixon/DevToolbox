using System;

namespace DevToolbox.Services.Models
{
    /// <summary>
    /// One in-flight file, for the per-file rows under the progress bar.
    /// <para>
    /// Carries a file <em>name</em> only — never a path — matching how <see cref="LogIngestProgress.CurrentFile"/>
    /// already reported a single file. <see cref="FileKey"/> is the exception: it is the caller's own
    /// handle to name this file back to <c>Skip</c>, not something a UI should display.
    /// </para>
    /// </summary>
    public sealed class FileProgressSnapshot
    {
        public required string FileKey { get; init; }
        public required string FileName { get; init; }
        public required string LocationName { get; init; }
        public FileIngestState State { get; init; }
        public long BytesRead { get; init; }
        public long BytesTotal { get; init; }
        public TimeSpan SinceLastAdvance { get; init; }
    }
}
