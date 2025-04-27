using Microsoft.AspNetCore.Components;
using DevToolbox.Services.Services;
using DevToolbox.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DevToolbox.UI.Pages
{
    public partial class PowerShellScripts : ComponentBase
    {
        private List<ScriptInfo> availableScripts = new();
        private string selectedScript = "";
        private string scriptText = "";
        private string output = "";
        private string error = "";
        private bool isExecuting = false;
        private string projectPath = "";
        private string newScriptName = "";

        [Inject] PowerShellService powerShellService {get; set;}
        private string searchText = "";
        
        protected override async Task OnInitializedAsync()
        {
            await LoadScripts();
        }
        
        private async Task LoadScripts()
        {
            availableScripts = powerShellService.GetAvailableScripts().ToList();
            
            // If there are scripts, load the first one
            if (availableScripts.Any() && string.IsNullOrEmpty(selectedScript))
            {
                await LoadScript(availableScripts.First().Name);
            }
        }
        
        private async Task LoadScript(string name)
        {
            selectedScript = name;
            var content = await powerShellService.GetScriptContentAsync(name);
            
            if (content != null)
            {
                scriptText = content;
            }
            else
            {
                scriptText = "";
                error = $"Could not load script '{name}'.";
            }
        }
        
        private IEnumerable<ScriptInfo> FilteredScripts => availableScripts
            .Where(s => string.IsNullOrEmpty(searchText) || 
                        s.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
            
        private async Task SaveScript()
        {
            if (string.IsNullOrEmpty(selectedScript))
            {
                return;
            }
            
            if (await powerShellService.SaveScriptAsync(selectedScript, scriptText))
            {
                await LoadScripts();
                output = $"Script '{selectedScript}' saved successfully.";
                error = "";
            }
            else
            {
                error = $"Failed to save script '{selectedScript}'.";
            }
        }
        
        private async Task DeleteScript()
        {
            if (string.IsNullOrEmpty(selectedScript))
            {
                return;
            }
            
            if (powerShellService.DeleteScript(selectedScript))
            {
                var deleted = selectedScript;
                selectedScript = "";
                scriptText = "";
                await LoadScripts();
                output = $"Script '{deleted}' deleted successfully.";
                error = "";
            }
            else
            {
                error = $"Failed to delete script '{selectedScript}'.";
            }
        }
        
        private async Task CreateNewScript()
        {
            // Create a simple script template
            selectedScript = "NewScript" + DateTime.Now.ToString("yyyyMMddHHmmss");
            scriptText = "# New PowerShell Script\n# Created: " + DateTime.Now.ToString("g") + "\n\n# Add your code here\n";
            
            // Save it immediately
            await SaveScript();
        }
        
        private async Task ExecuteScript()
        {
            if (string.IsNullOrWhiteSpace(scriptText))
                return;
                
            isExecuting = true;
            try
            {
                (output, error) = await powerShellService.ExecuteScriptAsync(scriptText);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
            finally
            {
                isExecuting = false;
            }
        }
        
        private async Task ExecuteScriptWithParams()
        {
            if (string.IsNullOrWhiteSpace(scriptText) || string.IsNullOrWhiteSpace(projectPath))
                return;
                
            isExecuting = true;
            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "ProjectPath", projectPath }
                };
                
                (output, error) = await powerShellService.ExecuteScriptWithParametersAsync(scriptText, parameters);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
            finally
            {
                isExecuting = false;
            }
        }
        
        private async Task RunCleanArtifacts()
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return;
                
            isExecuting = true;
            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "ProjectPath", projectPath }
                };
                
                (output, error) = await powerShellService.ExecuteScriptFileAsync("CleanBuildArtifacts", parameters);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
            finally
            {
                isExecuting = false;
            }
        }
        
        private void ClearOutput()
        {
            output = "";
            error = "";
        }
    }
}