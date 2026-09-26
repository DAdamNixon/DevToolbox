using System.ComponentModel;
using System.Diagnostics;

namespace DevToolbox.UI.Linux;

/// <summary>
/// The tray's questions and warnings, the Linux counterpart of the Windows tray's MessageBoxes.
/// <para>
/// zenity, which the folder pickers already use: every desktop can run it, and it runs in a process
/// of its own, so the tray's thread — which is the window's GTK thread — never waits on a person.
/// Each method says when zenity is missing, so the caller can pick a safe way round it.
/// </para>
/// </summary>
internal static class Dialogs
{
    /// <summary>Yes or no, with No the default. Null when no dialog could be shown.</summary>
    public static async Task<bool?> AskAsync(string title, string text, string yes, string no) =>
        await RunAsync(["--question", "--icon=dialog-warning", "--default-cancel", $"--ok-label={yes}", $"--cancel-label={no}"], title, text)
            is { } exitCode ? exitCode == 0 : null;

    /// <summary>A warning with an OK button. False when no dialog could be shown.</summary>
    public static async Task<bool> WarnAsync(string title, string text) =>
        await RunAsync(["--warning"], title, text) is not null;

    private static async Task<int?> RunAsync(string[] kind, string title, string text)
    {
        var startInfo = new ProcessStartInfo("zenity") { UseShellExecute = false };
        foreach (var argument in kind) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add($"--title={title}");
        startInfo.ArgumentList.Add($"--text={text}");
        // Group and option names come from the hosts file, so they are shown as text, never as markup.
        startInfo.ArgumentList.Add("--no-markup");
        startInfo.ArgumentList.Add("--width=420");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return null; // Not installed.
        }
    }
}
