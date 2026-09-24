namespace DevToolbox.Services.Models;

/// <summary>
/// Which PowerShell stream a line of output came from. The console colours and marks each one
/// differently, and only <see cref="Error"/> counts towards "finished with errors".
/// </summary>
public enum ScriptOutputKind
{
    /// <summary>An object the script wrote to the pipeline, including a native program's stdout.</summary>
    Output,

    /// <summary><c>Write-Host</c> and <c>Write-Information</c>, the way every bundled script reports progress.</summary>
    Host,

    /// <summary><c>Write-Warning</c>.</summary>
    Warning,

    /// <summary><c>Write-Error</c>, a failed cmdlet, or the terminating error that ended the run.</summary>
    Error,

    /// <summary>
    /// A line a native program wrote to stderr. Not an error: npm writes its warnings and progress
    /// there, git writes its progress there, and showing each one as a failure made a successful
    /// npm install look like nine failed ones.
    /// </summary>
    NativeStderr,

    /// <summary><c>Write-Verbose</c> and <c>Write-Debug</c>, when the script's preferences let them through.</summary>
    Verbose
}

/// <summary>
/// One line of a running script's output, delivered as it happens rather than at the end.
/// </summary>
/// <param name="Kind">The stream it came from.</param>
/// <param name="Text">The text, without a trailing newline.</param>
/// <param name="Color">
/// <c>Write-Host -ForegroundColor</c>, when the script chose one. Null for everything else, which the
/// console styles by <paramref name="Kind"/>.
/// </param>
public sealed record ScriptOutputLine(ScriptOutputKind Kind, string Text, ConsoleColor? Color = null);

/// <summary>How a run ended.</summary>
public enum ScriptRunOutcome
{
    /// <summary>It ran to the end. It may still have written errors along the way.</summary>
    Completed,

    /// <summary>It did not start, or a terminating error ended it early.</summary>
    Failed,

    /// <summary>Stop was pressed.</summary>
    Stopped
}
