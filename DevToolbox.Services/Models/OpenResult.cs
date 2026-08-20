namespace DevToolbox.Services.Models;

/// <summary>
/// Outcome of an "open this somewhere" request.
/// <para>
/// These used to be <c>void</c> with the exception written to <c>Console</c>, which on a
/// WinForms app goes nowhere — a failed open looked exactly like a click that did not
/// register. Returning the reason lets the UI say what went wrong.
/// </para>
/// </summary>
public class OpenResult
{
    public bool Success { get; init; }

    /// <summary>Human-readable reason, set only when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    public static OpenResult Ok() => new() { Success = true };

    public static OpenResult Fail(string error) => new() { Success = false, Error = error };
}
