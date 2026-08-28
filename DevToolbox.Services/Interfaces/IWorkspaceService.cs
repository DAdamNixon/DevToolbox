using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    public interface IWorkspaceService
    {
        List<WorkspaceGroup> WorkspaceGroups { get; }
        Task<List<WorkspaceGroup>> GetWorkspaceGroupsAsync();
        Task SaveWorkspaceGroupsAsync(List<WorkspaceGroup> groups);
        Task<OpenResult> OpenWorkspaceLocationAsync(Workspace workspace, WorkspaceLocation location);
        Task<OpenResult> OpenLocationInExplorerAsync(WorkspaceLocation location);
        Task<OpenResult> OpenLocationInTerminalAsync(WorkspaceLocation location);

        /// <summary>
        /// Opens a location in VS Code. A folder opens as itself and a <c>.code-workspace</c> as the
        /// workspace it describes — the one file type VS Code reads as a project rather than as
        /// text. Every other file opens the folder containing it, because the point is to get at
        /// the project rather than to read a <c>.sln</c> in an editor tab.
        /// </summary>
        Task<OpenResult> OpenLocationInVsCodeAsync(WorkspaceLocation location);
        Task<OpenResult> OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option);
        Task<OpenResult> RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location);
        Task<WorkspaceGroup> CreateWorkspaceGroupAsync(string name);
        Task<Workspace> CreateWorkspaceAsync(string name, string groupName);
    }
}
