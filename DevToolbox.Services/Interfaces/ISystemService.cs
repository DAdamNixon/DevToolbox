using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    /// <summary>
    /// Interface for system-level operations like opening files, folders, and executing external processes
    /// </summary>
    public interface ISystemService
    {
        /// <summary>
        /// Opens a file or folder location using the default system application
        /// </summary>
        Task<OpenResult> OpenLocationAsync(string path);

        /// <summary>
        /// Opens a folder in Windows Explorer
        /// </summary>
        Task<OpenResult> OpenInExplorerAsync(string path);

        /// <summary>
        /// Opens a folder in Windows Terminal
        /// </summary>
        Task<OpenResult> OpenInTerminalAsync(string path);

        /// <summary>
        /// Opens a location with a custom application.
        /// </summary>
        /// <param name="line">
        /// Optional 1-based line to jump to, substituted into the option's
        /// <c>arguments</c> as <c>{1}</c>. Which switch that needs is the editor's
        /// business and stays in config — <c>-g "{0}:{1}"</c> for VS Code,
        /// <c>-n{1} "{0}"</c> for Notepad++ — so nothing here knows about editors.
        /// Ignored when the option has no argument template.
        /// </param>
        Task<OpenResult> OpenWithCustomAppAsync(string path, CustomOpenOption option, int? line = null);

        /// <summary>
        /// Runs a configured command to completion and reports its exit code and output.
        /// <para>
        /// Unlike the Open methods, this waits. It exists for commands whose result matters
        /// rather than commands that hand off to another program — flushing the DNS cache after
        /// a hosts-file change being the motivating case, where "launched" tells the user
        /// nothing useful.
        /// </para>
        /// <para>
        /// There is no path or line to substitute, so <c>{0}</c> and <c>{1}</c> in
        /// <see cref="CustomOpenOption.Arguments"/> are not replaced. The executable is located
        /// exactly as it is for an Open — locator command, then explicit path, then PATH ×
        /// PATHEXT, then App Paths.
        /// </para>
        /// </summary>
        Task<CommandResult> RunToCompletionAsync(CustomOpenOption option, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a PowerShell script with the given parameters
        /// </summary>
        Task<OpenResult> ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters);
    }
}
