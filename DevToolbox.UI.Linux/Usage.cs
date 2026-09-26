namespace DevToolbox.UI.Linux;

/// <summary><c>devtoolbox --help</c>: the window's own switches, then the command line's commands.</summary>
internal static class Usage
{
    public static async Task<int> PrintAsync()
    {
        Console.Out.WriteLine("""
            Usage:
              devtoolbox                 Open DevToolbox, or bring up the window of the copy already running
              devtoolbox --no-window     Start it without a window (for starting at login)
              devtoolbox --quit          Stop the running copy
              devtoolbox projects …      Find and open projects from the terminal
              devtoolbox logs …          Search logs from the terminal

            Add --help after a command for its options, e.g. devtoolbox logs search --help

            """);

        // The command line's own list, so the two never disagree about what exists.
        return await DevToolbox.Cli.Cli.RunAsync(["--help"]);
    }
}
