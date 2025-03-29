using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    public interface IWorkspaceService
    {
        List<WorkspaceGroup> WorkspaceGroups { get; }
        Task<List<WorkspaceGroup>> GetWorkspaceGroupsAsync();
        Task SaveWorkspaceGroupsAsync(List<WorkspaceGroup> groups);
        Task OpenWorkspaceLocationAsync(Workspace workspace, WorkspaceLocation location);
        Task OpenLocationInExplorerAsync(WorkspaceLocation location);
        Task OpenLocationInTerminalAsync(WorkspaceLocation location);
        Task OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option);
        Task RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location);
        Task<WorkspaceGroup> CreateWorkspaceGroupAsync(string name);
        Task<Workspace> CreateWorkspaceAsync(string name, string groupName);
    }
} 