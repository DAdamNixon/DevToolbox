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
        Task<OpenResult> OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option);
        Task<OpenResult> RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location);
        Task<WorkspaceGroup> CreateWorkspaceGroupAsync(string name);
        Task<Workspace> CreateWorkspaceAsync(string name, string groupName);
    }
}
