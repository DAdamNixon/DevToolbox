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
        /// Executes a PowerShell script with the given parameters
        /// </summary>
        Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters);
    }
}
