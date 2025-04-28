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
        
        // Validation related properties
        private bool enableScriptValidation = true;
        private List<string> validationErrors = new();
        private List<string> validationWarnings = new();
        private bool showValidationResults = false;

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
            
            // Clear validation results when loading a new script
            ClearValidation();
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
            
            ClearValidation();
            
            var result = await powerShellService.SaveScriptAsync(selectedScript, scriptText, enableScriptValidation);
            
            if (result.Success)
            {
                await LoadScripts();
                output = $"Script '{selectedScript}' saved successfully.";
                error = "";
                
                // Show validation warnings if any
                if (result.ValidationResult != null)
                {
                    validationWarnings = result.ValidationResult.ValidationWarnings;
                    validationErrors = result.ValidationResult.ValidationErrors;
                    showValidationResults = validationWarnings.Any() || validationErrors.Any();
                }
            }
            else
            {
                error = $"Failed to save script '{selectedScript}'. {result.ErrorMessage}";
                
                // Show validation errors if any
                if (result.ValidationResult != null)
                {
                    validationWarnings = result.ValidationResult.ValidationWarnings;
                    validationErrors = result.ValidationResult.ValidationErrors;
                    showValidationResults = true;
                }
            }
        }
        
        private void ClearValidation()
        {
            validationErrors.Clear();
            validationWarnings.Clear();
            showValidationResults = false;
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
            // Create a simple script template from the ScriptTemplate.ps1
            string templateName = "ScriptTemplate";
            var templateContent = await powerShellService.GetScriptContentAsync(templateName);
            
            if (string.IsNullOrEmpty(templateContent))
            {
                // Fallback to basic template if template file doesn't exist
                templateContent = 
@"param(
    [Parameter(Mandatory=$true)]
    [string]$ProjectPath,
    [string]$LocationPath,
    [string]$filePath,
    [string]$RootDirectory
)

# New PowerShell Script
# Created: " + DateTime.Now.ToString("g") + @"

# Script logic goes here

# Keep window open for viewing
Write-Host ""`nPress any key to close this window...""
$null = $Host.UI.RawUI.ReadKey(""NoEcho,IncludeKeyDown"")";
            }
            
            selectedScript = "NewScript" + DateTime.Now.ToString("yyyyMMddHHmmss");
            scriptText = templateContent;
            
            // Save it immediately, but don't validate it since it's a template
            var result = await powerShellService.SaveScriptAsync(selectedScript, scriptText, false);
            
            if (result.Success)
            {
                await LoadScripts();
                output = $"New script '{selectedScript}' created successfully.";
                error = "";
            }
            else
            {
                error = $"Failed to create script: {result.ErrorMessage}";
            }
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
        
        private void ClearOutput()
        {
            output = "";
            error = "";
        }
        
        private void ToggleValidation()
        {
            enableScriptValidation = !enableScriptValidation;
        }
        
        private async Task ValidateCurrentScript()
        {
            if (string.IsNullOrEmpty(scriptText))
                return;
                
            ClearValidation();
            
            var validator = new ScriptValidationService();
            var result = validator.ValidateScript(scriptText);
            
            validationErrors = result.ValidationErrors;
            validationWarnings = result.ValidationWarnings;
            showValidationResults = true;
            
            if (result.IsValid && !result.HasWarnings)
            {
                output = "Script validation passed successfully!";
                error = "";
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
    }
}