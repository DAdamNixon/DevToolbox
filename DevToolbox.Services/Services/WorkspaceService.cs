using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System.Diagnostics;

namespace DevToolbox.Services.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly PowerShellService _powerShellService;
        private readonly string _workspaceGroupsKey = "workspaceGroups";
        private readonly string _customOpenOptionsKey = "customOpenOptions";
        private List<WorkspaceGroup> _workspaceGroups = new();

        public List<WorkspaceGroup> WorkspaceGroups => _workspaceGroups;

        public WorkspaceService(IYamlStorageService yamlStorage, PowerShellService powerShellService)
        {
            _yamlStorage = yamlStorage;
            _powerShellService = powerShellService;
            _ = LoadWorkspaceGroupsAsync();
        }

        private async Task LoadWorkspaceGroupsAsync()
        {
            _workspaceGroups = (await GetWorkspaceGroupsAsync()).ToList();
        }

        public async Task<IEnumerable<WorkspaceGroup>> GetWorkspaceGroupsAsync()
        {
            return await _yamlStorage.LoadAsync<List<WorkspaceGroup>>(_workspaceGroupsKey) ?? new List<WorkspaceGroup>();
        }

        public async Task SaveWorkspaceGroupsAsync(List<WorkspaceGroup> groups)
        {
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
    }
} 