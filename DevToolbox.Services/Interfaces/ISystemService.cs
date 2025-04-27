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
        Task OpenLocationAsync(string path);

        /// <summary>
        /// Opens a folder in Windows Explorer
        /// </summary>
        Task OpenInExplorerAsync(string path);

        /// <summary>
        /// Opens a folder in Windows Terminal
        /// </summary>
        Task OpenInTerminalAsync(string path);

        /// <summary>
        /// Opens a location with a custom application
        /// </summary>
        Task OpenWithCustomAppAsync(string path, CustomOpenOption option);

        /// <summary>
        /// Executes a PowerShell script with the given parameters
        /// </summary>
        Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters);
    }
} 