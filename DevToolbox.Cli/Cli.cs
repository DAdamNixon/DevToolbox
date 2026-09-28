using System.CommandLine;

namespace DevToolbox.Cli;

/// <summary>
/// The command tree. Public so the Linux host can hand <c>devtoolbox projects …</c> and
/// <c>devtoolbox logs …</c> straight here, making one command for the window and the terminal.
/// </summary>
public static class Cli
{
    /// <summary>The first words that mean "the command line, not the window".</summary>
    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(StringComparer.Ordinal) { "projects", "logs" };

    public static Task<int> RunAsync(string[] args) => Build().Parse(args).InvokeAsync();

    internal static RootCommand Build()
    {
        var root = new RootCommand(
            "DevToolbox from a terminal: find and open projects, and search logs, with the same config as the app.");

        root.Subcommands.Add(ProjectsCommands.Build());
        root.Subcommands.Add(LogsCommands.Build());

        return root;
    }

    /// <summary>Every command takes it: machine-readable output instead of a table.</summary>
    internal static Option<bool> JsonOption() => new("--json") { Description = "Print JSON instead of a table." };
}
