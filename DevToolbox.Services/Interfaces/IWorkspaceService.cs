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
        /// Opens a location's <em>folder</em> in VS Code. A folder location opens as itself; a file
        /// location opens the directory containing it, because the point of this is to get at the
        /// project rather than to read a <c>.sln</c> as text.
        /// </summary>
        Task<OpenResult> OpenLocationInVsCodeAsync(WorkspaceLocation location);
        Task<OpenResult> OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option);
        Task<OpenResult> RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location);
        Task<WorkspaceGroup> CreateWorkspaceGroupAsync(string name);
        Task<Workspace> CreateWorkspaceAsync(string name, string groupName);
    }
}
