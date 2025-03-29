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
        private readonly string _workspaceGroupsKey = "workspaceGroups";
        private readonly string _customOpenOptionsKey = "customOpenOptions";
        private List<WorkspaceGroup> _workspaceGroups = new();
        private int _nextGroupId = 1;
        private int _nextWorkspaceId = 1;

        public List<WorkspaceGroup> WorkspaceGroups => _workspaceGroups;

        public WorkspaceService(IYamlStorageService yamlStorage, PowerShellService powerShellService, IConfiguration configuration)
        {
            _yamlStorage = yamlStorage;
            _powerShellService = powerShellService;
            _ = LoadWorkspaceGroupsAsync();
        }

        private async Task LoadWorkspaceGroupsAsync()
        {
            Console.WriteLine("Loading workspace groups...");
            _workspaceGroups = await GetWorkspaceGroupsAsync();
            Console.WriteLine($"Loaded {_workspaceGroups.Count} groups");
            foreach (var group in _workspaceGroups)
            {
                Console.WriteLine($"Group: {group.Name} (ID: {group.Id}) with {group.Workspaces.Count} workspaces");
                foreach (var workspace in group.Workspaces)
                {
                    Console.WriteLine($"  Workspace: {workspace.Name} (ID: {workspace.Id})");
                }
            }
            UpdateNextIds();
        }

        private void UpdateNextIds()
        {
            _nextGroupId = _workspaceGroups.Any() ? 
                _workspaceGroups.Max(g => g.Id) + 1 : 1;
            
            _nextWorkspaceId = _workspaceGroups.Any() ? 
                _workspaceGroups.SelectMany(g => g.Workspaces).Max(w => w.Id) + 1 : 1;
        }

        public async Task<List<WorkspaceGroup>> GetWorkspaceGroupsAsync()
        {
            Console.WriteLine("Loading from YAML storage...");
            var groups = await _yamlStorage.LoadAsync<List<WorkspaceGroup>>(_workspaceGroupsKey) ?? new List<WorkspaceGroup>();
            Console.WriteLine($"Loaded {groups.Count} groups from storage");
            
            // Ensure all groups and workspaces have IDs
            foreach (var group in groups)
            {
                if (group.Id == 0)
                {
                    group.Id = _nextGroupId++;
                    Console.WriteLine($"Assigned new ID {group.Id} to group {group.Name}");
                }
                foreach (var workspace in group.Workspaces)
                {
                    if (workspace.Id == 0)
                    {
                        workspace.Id = _nextWorkspaceId++;
                        Console.WriteLine($"Assigned new ID {workspace.Id} to workspace {workspace.Name}");
                    }
                }
            }
            
            _workspaceGroups = groups;
            return groups;
        }

        public async Task SaveWorkspaceGroupsAsync(List<WorkspaceGroup> groups)
        {
            // Ensure all groups and workspaces have IDs
            foreach (var group in groups)
            {
                if (group.Id == 0)
                {
                    group.Id = _nextGroupId++;
                }
                foreach (var workspace in group.Workspaces)
                {
                    if (workspace.Id == 0)
                    {
                        workspace.Id = _nextWorkspaceId++;
                    }
                }
            }

            await _yamlStorage.SaveAsync(_workspaceGroupsKey, groups);
            _workspaceGroups = groups;
        }

        public async Task<GlobalCustomOpenOptions> GetGlobalCustomOpenOptionsAsync()
        {
            return await _yamlStorage.LoadAsync<GlobalCustomOpenOptions>(_customOpenOptionsKey) ?? new GlobalCustomOpenOptions();
        }

        public Task<List<ScriptInfo>> GetAvailableScriptsAsync()
        {
            return Task.FromResult(_powerShellService.GetAvailableScripts().ToList());
        }

        public async Task OpenWorkspaceLocationAsync(Workspace workspace, WorkspaceLocation location)
        {
            await Task.Run(() =>
            {
                if (File.Exists(location.Path))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = location.Path,
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                }
                else if (Directory.Exists(location.Path))
                {
                    Process.Start("explorer.exe", location.Path);
                }
            });
        }

        public async Task OpenLocationInExplorerAsync(WorkspaceLocation location)
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(location.Root))
                {
                    Process.Start("explorer.exe", location.Root);
                }
            });
        }

        public async Task OpenLocationInTerminalAsync(WorkspaceLocation location)
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(location.Root))
                {
                    Process.Start("wt.exe", $"-d \"{location.Root}\"");
                }
            });
        }

        public async Task OpenLocationWithCustomAppAsync(Workspace workspace, WorkspaceLocation location, CustomOpenOption option)
        {
            await Task.Run(() =>
            {
                if (option.Type == OpenOptionType.Executable)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = option.ExecutablePath,
                        Arguments = string.Format(option.Arguments, location.Root),
                        UseShellExecute = false
                    };
                    Process.Start(startInfo);
                }
                else
                {
                    var command = string.Format(option.Command ?? "", location.Root);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -Command \"{command}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    Process.Start(startInfo);
                }
            });
        }

        public async Task RunScriptOnLocationAsync(ScriptInfo script, Workspace workspace, WorkspaceLocation location)
        {
            if (Directory.Exists(location.Path))
            {
                var parameters = new Dictionary<string, object>
                {
                    { "ProjectPath", location.Root }
                };

                await _powerShellService.ExecuteScriptFileAsync(script.Name, parameters);
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