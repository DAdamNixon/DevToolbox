using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace DevToolbox.Services.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly PowerShellService _powerShellService;
        private readonly ISystemService _systemService;
        private readonly string _workspaceGroupsKey = "workspaceGroups";
        private readonly string _customOpenOptionsKey = "customOpenOptions";
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private List<WorkspaceGroup> _workspaceGroups = new();
        private int _nextGroupId = 1;
        private int _nextWorkspaceId = 1;

        public List<WorkspaceGroup> WorkspaceGroups => _workspaceGroups;

        public WorkspaceService(IYamlStorageService yamlStorage, PowerShellService powerShellService,
                               ISystemService systemService, IConfiguration configuration)
        {
            _yamlStorage = yamlStorage;
            _powerShellService = powerShellService;
            _systemService = systemService;
            _ = LoadWorkspaceGroupsAsync();
        }

        private async Task LoadWorkspaceGroupsAsync()
        {
            _workspaceGroups = await GetWorkspaceGroupsAsync();
        }

        private void UpdateNextIds()
        {
            _nextGroupId = _workspaceGroups.Any() ?
                _workspaceGroups.Max(g => g.Id) + 1 : 1;

            var workspaces = _workspaceGroups.SelectMany(g => g.Workspaces).ToList();
            _nextWorkspaceId = workspaces.Any() ? workspaces.Max(w => w.Id) + 1 : 1;
        }

        public async Task<List<WorkspaceGroup>> GetWorkspaceGroupsAsync()
        {
            // The constructor kicks off a load and the dashboard asks for one too;
            // without this they can interleave and both try to repair ids at once.
            await _loadGate.WaitAsync();
            try
            {
                var groups = await _yamlStorage.LoadAsync<List<WorkspaceGroup>>(_workspaceGroupsKey) ?? new List<WorkspaceGroup>();

                _workspaceGroups = groups;
                var repaired = NormalizeIds(groups);
                UpdateNextIds();

                if (repaired)
                {
                    // Persist the repair so it happens once rather than on every load.
                    await _yamlStorage.SaveAsync(_workspaceGroupsKey, groups);
                }

                return groups;
            }
            finally
            {
                _loadGate.Release();
            }
        }

        /// <summary>
        /// Gives every group and workspace a unique positive id, renumbering any that are
        /// missing or duplicated, and returns whether anything had to change.
        /// <para>
        /// Ids in workspaceGroups.yaml drifted into being unique only *within* a group —
        /// hundreds of workspaces shared ids and three groups all had id 1 — because the
        /// old loader only filled in ids that were literally 0. Anything keyed on an id
        /// (expand state, dialogs, Blazor's @key) then aliased between unrelated cards.
        /// </para>
        /// </summary>
        private static bool NormalizeIds(List<WorkspaceGroup> groups)
        {
            var changed = false;

            var usedGroupIds = new HashSet<int>();
            var nextGroupId = 1;

            var usedWorkspaceIds = new HashSet<int>();
            var nextWorkspaceId = 1;

            foreach (var group in groups)
            {
                if (group.Id <= 0 || !usedGroupIds.Add(group.Id))
                {
                    while (!usedGroupIds.Add(nextGroupId))
                    {
                        nextGroupId++;
                    }

                    group.Id = nextGroupId;
                    changed = true;
                }

                foreach (var workspace in group.Workspaces)
                {
                    if (workspace.Id <= 0 || !usedWorkspaceIds.Add(workspace.Id))
                    {
                        while (!usedWorkspaceIds.Add(nextWorkspaceId))
                        {
                            nextWorkspaceId++;
                        }

                        workspace.Id = nextWorkspaceId;
                        changed = true;
                    }

                    // GroupName was drifting out of sync with the group the workspace
                    // actually sits in (Account claimed "Solutions" while living under
                    // ElliottElectric), which made it useless as an identifier.
                    if (workspace.GroupName != group.Name)
                    {
                        workspace.GroupName = group.Name;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                Console.WriteLine("WorkspaceService: repaired duplicate/missing ids in workspaceGroups.yaml");
            }

            return changed;
        }

        public async Task SaveWorkspaceGroupsAsync(List<WorkspaceGroup> groups)
        {
            NormalizeIds(groups);
            await _yamlStorage.SaveAsync(_workspaceGroupsKey, groups);
            _workspaceGroups = groups;
            UpdateNextIds();
        }

        public async Task<GlobalCustomOpenOptions> GetGlobalCustomOpenOptionsAsync()
        {
            return await _yamlStorage.LoadAsync<GlobalCustomOpenOptions>(_customOpenOptionsKey) ?? new GlobalCustomOpenOptions();
        }

        public Task<List<ScriptInfo>> GetAvailableScriptsAsync()
        {
            return Task.FromResult(_powerShellService.GetAvailableScripts().ToList());
        }

        public async Task<OpenResult> OpenWorkspaceLocationAsync(Workspace workspace, WorkspaceLocation location)
        {
            return await _systemService.OpenLocationAsync(location.Path);
        }

        public async Task<OpenResult> OpenLocationInExplorerAsync(WorkspaceLocation location)
        {
            return await _systemService.OpenInExplorerAsync(location.Root);
        }

        public async Task<OpenResult> OpenLocationInTerminalAsync(WorkspaceLocation location)
        {
            return await _systemService.OpenInTerminalAsync(location.Root);
        }

        public async Task<OpenResult> OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option)
        {
            return await _systemService.OpenWithCustomAppAsync(location.Root, option);
        }

        public async Task RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location)
        {
            if (Directory.Exists(location.Path))
            {
                try
                {
                    Console.WriteLine($"Executing script: {script.Name} on location: {location.Path}");

                    var parameters = new Dictionary<string, object>
                    {
                        { "ProjectPath", location.Root }
                    };

                    await _systemService.ExecuteScriptAsync(script.Name, parameters);
                    Console.WriteLine($"Script execution initiated.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing script: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"Location path does not exist: {location.Path}");
            }
        }

        public async Task<WorkspaceGroup> CreateWorkspaceGroupAsync(string name)
        {
            var group = new WorkspaceGroup
            {
                Id = _nextGroupId++,
                Name = name,
                Workspaces = new List<Workspace>()
            };

            _workspaceGroups.Add(group);
            await SaveWorkspaceGroupsAsync(_workspaceGroups);
            return group;
        }

        public async Task<Workspace> CreateWorkspaceAsync(string name, string groupName)
        {
            var workspace = new Workspace
            {
                Id = _nextWorkspaceId++,
                Name = name,
                GroupName = groupName,
                Locations = new List<WorkspaceLocation>()
            };

            var group = _workspaceGroups.FirstOrDefault(g => g.Name == groupName);
            if (group != null)
            {
                group.Workspaces.Add(workspace);
                await SaveWorkspaceGroupsAsync(_workspaceGroups);
            }

            return workspace;
        }
    }
}
