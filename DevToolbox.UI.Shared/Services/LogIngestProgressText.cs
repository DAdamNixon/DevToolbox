using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services;

/// <summary>
/// The words the Log Viewer puts on an ingest in progress — headline, estimate, per-file line.
/// <para>
/// Out of the page because two places say them now: the big loading card, while nothing has been
/// read yet, and the drawer at the top of the results once rows are streaming in. One copy is what
/// keeps "Reading 142 of 380 files" from reading two different ways depending on where you look.
/// </para>
/// </summary>
public static class LogIngestProgressText
{
    /// <summary>
    /// One line saying what is happening and how far in. Reads as
    /// "Reading 142 of 380 files · 1.2 GB of 3.4 GB".
    /// </summary>
    public static string Headline(LogIngestProgress? progress)
    {
        if (progress is not { } p) return "Preparing search…";

        if (p.Phase == LogIngestPhase.Querying) return "Querying results…";

        // Listing has no denominator — the directory size is unknown until the walk
        // ends — so it reports what it has looked at instead of a fraction.
        if (p.Phase == LogIngestPhase.Listing)
        {
            var where = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" in {p.CurrentFile}";
            return p.ItemsExamined == 0
                ? $"Listing files{where}…"
                : $"Listing files{where} — {p.ItemsExamined:N0} checked, {p.FilesDone:N0} matched";
        }

        var verb = p.Phase == LogIngestPhase.Scanning ? "Scanning" : "Reading";
        if (p.FilesTotal == 0) return $"{verb}…";

        var text = $"{verb} {p.FilesDone:N0} of {p.FilesTotal:N0} files";
        if (p.BytesTotal > 0) text += $" · {FormatBytes(p.BytesDone)} of {FormatBytes(p.BytesTotal)}";
        return text;
    }

    /// <summary>
    /// "~40s left · 1 stalled", or just "2 stalled" once nothing is moving — the reporter nulls
    /// Eta itself once every in-flight file is stalled, so this only has to add the count.
    /// </summary>
    /// <param name="includeStalled">
    /// False leaves the stall count out, for the drawer, which shows it as a button of its own.
    /// With nothing moving that leaves nothing to say, and the result is empty.
    /// </param>
    public static string Eta(LogIngestProgress? progress, bool includeStalled = true)
    {
        if (progress is not { } p) return "";
        // Listing has no total to work towards, so it gets no estimate — the other
        // phases now do, including Scanning, which measures itself in files.
        if (p.Phase is LogIngestPhase.Querying or LogIngestPhase.Listing) return "";

        // Null means the reporter does not yet have enough evidence. Saying so is
        // better than showing a number that is about to change by minutes.
        var eta = p.Eta is not { } remaining ? "estimating…" : $"~{FormatDuration(remaining)} left";
        var stalled = includeStalled && p.FilesStalled > 0 ? $"{p.FilesStalled} stalled" : "";

        // Every in-flight file stalled: the reporter withholds the estimate, so "estimating…"
        // would be a claim that something is being measured when nothing is moving. With only some
        // stalled, the others are still being measured, and "estimating…" is still true.
        var allStalled = p.InFlight.Count > 0 && p.FilesStalled >= p.InFlight.Count;
        if (!includeStalled && p.Eta is null && allStalled) return "";

        return (eta, stalled) switch
        {
            ("estimating…", "") => "estimating…",
            (_, "") => eta,
            _ when p.Eta is null => stalled,
            _ => $"{eta} · {stalled}"
        };
    }

    /// <summary>"File.txt — 1.2 MB of 3.4 MB", or just the bytes read when the size is unknown.</summary>
    public static string DescribeFile(FileProgressSnapshot file)
    {
        if (file.State == FileIngestState.Opening) return $"{file.FileName} — connecting…";

        var bytes = file.BytesTotal > 0
            ? $"{FormatBytes(file.BytesRead)} of {FormatBytes(file.BytesTotal)}"
            : FormatBytes(file.BytesRead);

        var stalled = file.State == FileIngestState.Stalled
            ? $" — last progress {(int)file.SinceLastAdvance.TotalSeconds}s ago"
            : "";

        return $"{file.FileName} — {bytes}{stalled}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 5) return "a few seconds";
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}
