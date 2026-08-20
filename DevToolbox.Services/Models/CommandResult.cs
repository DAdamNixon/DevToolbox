namespace DevToolbox.Services.Models;

/// <summary>
/// Outcome of a configured command that was run to completion.
/// <para>
/// Distinct from <see cref="OpenResult"/>, which only reports whether something was
/// <em>launched</em>. Some commands are worth waiting for — a DNS flush after a hosts-file
/// switch is only useful if it actually ran — and "started successfully" is not the same
/// answer as "exited zero".
/// </para>
/// </summary>
/// <param name="Started">The process was created. False means it could not be located or launched.</param>
/// <param name="ExitCode">Null when the process never started.</param>
/// <param name="Output">Trimmed standard output, for showing the user what happened.</param>
/// <param name="Error">Why it failed, or null on success.</param>
public sealed record CommandResult(bool Started, int? ExitCode, string Output, string? Error)
{
    public bool Success => Started && ExitCode == 0;

    public static CommandResult Failed(string error) => new(false, null, string.Empty, error);

    /// <summary>A short line suitable for a status banner.</summary>
    public string Describe() => Success
        ? Output.Length > 0 ? Output : "Completed."
        : Error ?? $"Exited with code {ExitCode}.";
}
