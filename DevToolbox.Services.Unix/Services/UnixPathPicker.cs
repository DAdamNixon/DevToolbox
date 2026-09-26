using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Native folder and file dialogs on Linux (zenity, which GNOME ships and every desktop can run)
/// and macOS (AppleScript's choose folder / choose file).
/// <para>
/// The dialog is a separate process, so it opens on the desktop whether the page asking is in an
/// app window or an ordinary browser tab. It opens where the server runs, which for DevToolbox is
/// always this machine: the server only listens on loopback.
/// </para>
/// </summary>
public sealed class UnixPathPicker : IPathPicker
{
    private const string NoDialog =
        "No folder dialog is available — install zenity (sudo apt install zenity), or type the path.";

    public Task<string?> PickFolderAsync(FolderPickerOptions options) =>
        OperatingSystem.IsMacOS()
            ? AppleScriptAsync("choose folder", options.Title, options.StartIn)
            : ZenityAsync(["--directory"], options.Title, options.StartIn);

    public Task<string?> PickFileAsync(FilePickerOptions options) =>
        OperatingSystem.IsMacOS()
            ? AppleScriptAsync("choose file", options.Title, options.StartIn)
            : ZenityAsync(ZenityFilters(options.Filter), options.Title, options.StartIn);

    private static async Task<string?> ZenityAsync(IEnumerable<string> extra, string title, string? startIn)
    {
        var zenity = UnixCommands.Resolve("zenity") ?? throw new InvalidOperationException(NoDialog);

        var arguments = new List<string> { "--file-selection", $"--title={title}" };
        arguments.AddRange(extra);

        // The trailing slash is what makes zenity open inside the folder instead of selecting it
        // in its parent.
        if (startIn is not null) arguments.Add($"--filename={startIn.TrimEnd('/')}/");

        var (exitCode, output, error) = await UnixCommands.RunAsync(zenity, arguments).ConfigureAwait(false);

        return exitCode switch
        {
            0 => output.TrimEnd('\n'),
            1 => null, // Cancelled, or the window was closed.
            _ => throw new InvalidOperationException($"The folder dialog failed: {error.Trim()}"),
        };
    }

    /// <summary>
    /// Windows Forms filter text as zenity filters: <c>Scripts|*.ps1;*.psm1|All Files (*.*)|*.*</c>
    /// becomes <c>--file-filter=Scripts | *.ps1 *.psm1</c> and <c>--file-filter=All Files (*.*) | *</c>.
    /// </summary>
    internal static IEnumerable<string> ZenityFilters(string filter)
    {
        var parts = (filter ?? string.Empty).Split('|');

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p == "*.*" ? "*" : p);

            yield return $"--file-filter={parts[i].Trim()} | {string.Join(' ', patterns)}";
        }
    }

    private static async Task<string?> AppleScriptAsync(string verb, string title, string? startIn)
    {
        var osascript = UnixCommands.Resolve("osascript") ?? throw new InvalidOperationException(NoDialog);

        var script = $"POSIX path of ({verb} with prompt \"{Escape(title)}\"" +
                     (startIn is null ? "" : $" default location POSIX file \"{Escape(startIn)}\"") + ")";

        var (exitCode, output, error) = await UnixCommands.RunAsync(osascript, ["-e", script]).ConfigureAwait(false);

        if (exitCode == 0) return output.TrimEnd('\n');

        // -128 is "User canceled".
        if (error.Contains("-128", StringComparison.Ordinal)) return null;

        throw new InvalidOperationException($"The folder dialog failed: {error.Trim()}");

        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
