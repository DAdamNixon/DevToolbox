using System.ComponentModel;
using System.Diagnostics;

namespace DevToolbox.Services.Services;

/// <summary>
/// Finding and starting programs the way a Unix shell would, for the rest of this project.
/// </summary>
internal static class UnixCommands
{
    /// <summary>
    /// A program name or path resolved to a file that exists, or null.
    /// <para>
    /// A value containing a slash is a path, with <c>~</c> and environment variables expanded; a
    /// bare name is looked up across <c>PATH</c>. Unlike Windows there is no extension list to try:
    /// <c>code</c> on Linux is a file called <c>code</c>.
    /// </para>
    /// </summary>
    public static string? Resolve(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;

        var candidate = ExpandHome(Environment.ExpandEnvironmentVariables(nameOrPath.Trim().Trim('"', '\'')));

        if (candidate.Contains('/'))
        {
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(directory, candidate);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    /// <summary>The first of <paramref name="names"/> that is installed.</summary>
    public static string? FirstInstalled(params string?[] names) =>
        names.Select(Resolve).FirstOrDefault(found => found is not null);

    /// <summary><c>~/x</c> as a full path; anything else unchanged.</summary>
    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;

    /// <summary>
    /// Starts a program and lets it run on its own. Its output is not captured: a GUI program
    /// writes nothing worth reading, and a pipe nobody drains is how a child ends up blocked.
    /// </summary>
    public static void StartDetached(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var _ = Process.Start(startInfo);
    }

    /// <summary>
    /// Runs a program to completion and returns what it said. Both streams are drained before the
    /// wait, because a child that fills a redirected pipe blocks until somebody reads it.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} did not start.");

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw;
        }

        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>Whether an exception from <see cref="Process.Start()"/> means "that program is not here".</summary>
    public static bool IsNotFound(Exception ex) => ex is Win32Exception or FileNotFoundException;
}
