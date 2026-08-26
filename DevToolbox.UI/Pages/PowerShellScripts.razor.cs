using Microsoft.AspNetCore.Components;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Models;
using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Only used by a script that declares no parameters at all, where it is set as the variable
        /// <c>$ProjectPath</c>. Everything with a param block is driven by the form instead.
        /// </summary>
        private string projectPath = "";

        /// <summary>The naming dialog, shown instead of inventing a timestamped file name.</summary>
        private bool newScriptVisible;

        /// <summary>Where the path pickers open from, per Settings. Read once at startup.</summary>
        private string defaultWorkspaceLocation = "";

        // Validation related properties
        private bool enableScriptValidation = true;
        private List<string> validationErrors = new();
        private List<string> validationWarnings = new();
        private bool showValidationResults = false;

        [Inject] PowerShellService powerShellService { get; set; } = null!;
        [Inject] IUiSettingsService uiSettings { get; set; } = null!;
        private string searchText = "";

        protected override async Task OnInitializedAsync()
        {
            defaultWorkspaceLocation = (await uiSettings.GetAsync()).DefaultWorkspaceLocation;
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

            // A different script asks for different things, so nothing typed for the last one
            // carries over. Cleared before the rebuild rather than merged: two scripts sharing a
            // parameter name do not share a value, and inheriting one silently is worse than
            // retyping it.
            parameterValues.Clear();
            SyncParameters();
            showMissingRequired = false;

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
        
        // --- creating a script ---

        /// <summary>The names in the sidebar, so the dialog can refuse one that is taken.</summary>
        private IReadOnlyCollection<string> ScriptNames =>
            availableScripts.Select(s => s.Name).ToList();

        /// <summary>
        /// Opens the naming dialog. It used to create "NewScript20260826122108" on the spot and save
        /// it — a library of timestamped files with no way to rename one, since this tab can save and
        /// delete but not move.
        /// </summary>
        private void CreateNewScript() => newScriptVisible = true;

        /// <summary>
        /// Creates the script the dialog named. The template is the bundled ScriptTemplate.ps1 when
        /// there is one, so a house style stays editable as a file rather than as a string in here.
        /// </summary>
        private async Task CreateScriptNamed(string name)
        {
            var templateContent = await powerShellService.GetScriptContentAsync("ScriptTemplate");

            if (string.IsNullOrEmpty(templateContent))
            {
                templateContent = DefaultTemplate();
            }

            // Saved without validation: a template is a starting point, and refusing to create a
            // file because its placeholder body has no output would be absurd.
            var result = await powerShellService.SaveScriptAsync(name, templateContent, false);

            if (!result.Success)
            {
                error = $"Failed to create script: {result.ErrorMessage}";
                return;
            }

            selectedScript = name;
            scriptText = templateContent;
            SyncParameters();
            ClearValidation();

            await LoadScripts();
            output = $"Created '{name}'.";
            error = "";
        }

        /// <summary>
        /// The fallback body, for a build with no bundled ScriptTemplate.ps1. ProjectPath is
        /// mandatory and named as the rest of the library names it, so the new script is runnable
        /// from the workspace card's Run Script menu without being edited first.
        /// </summary>
        private static string DefaultTemplate() =>
            """
            param(
                [Parameter(Mandatory = $true, HelpMessage = 'The folder this script works on')]
                [string]$ProjectPath
            )

            # Report progress with Write-Host; the Scripts tab and the terminal window both show it.
            Write-Host "Working on $ProjectPath"

            Write-Host "Done."
            """;

        // --- parameters ---

        /// <summary>
        /// The fields on screen, and what has been typed into each. Rebuilt from the script text
        /// rather than configured, so the form and the script cannot disagree.
        /// </summary>
        private List<ScriptParameter> parameters = new();
        private readonly Dictionary<string, string> parameterValues = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A Run has been asked for while a required value was empty. Gates the notice under the
        /// form: before you press anything, an empty required field is not yet a mistake.
        /// </summary>
        private bool showMissingRequired;

        /// <summary>
        /// Re-reads the param() block and reconciles what has been typed with it.
        /// <para>
        /// Values are kept across the rebuild, which is the whole point of doing it this way: adding
        /// a parameter to a script you are half way through configuring must not clear the three
        /// fields you already filled in.
        /// </para>
        /// </summary>
        private void SyncParameters()
        {
            parameters = PowerShellService.DeclaredParameters(scriptText).ToList();

            var live = parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in parameterValues.Keys.Where(k => !live.Contains(k)).ToList())
            {
                parameterValues.Remove(gone);
            }

            foreach (var parameter in parameters)
            {
                // Only seed what has never been touched: a default is a suggestion, and re-applying
                // it over an edit would undo the edit on the next keystroke in the editor.
                if (!parameterValues.ContainsKey(parameter.Name))
                {
                    parameterValues[parameter.Name] = parameter.DefaultValue;
                }
            }
        }

        private string Value(string name) => parameterValues.GetValueOrDefault(name, string.Empty);

        private bool IsChecked(string name) =>
            bool.TryParse(Value(name), out var on) && on;

        private void SetValue(string name, string? value) =>
            parameterValues[name] = value ?? string.Empty;

        /// <summary>
        /// Required parameters still sitting empty. What stops the Run button, and the only thing
        /// standing between a click and a PowerShell binding error nobody can read.
        /// </summary>
        private List<string> MissingRequired =>
            parameters
                .Where(p => p.IsMandatory && p.Kind != ScriptParameterKind.Switch)
                .Where(p => string.IsNullOrWhiteSpace(Value(p.Name)))
                .Select(p => p.Label)
                .ToList();

        /// <summary>
        /// How the parameter is declared, for the hint under its field — the type and the default,
        /// which is the bit of the script you would otherwise scroll up to check.
        /// </summary>
        private static string ParameterSignature(ScriptParameter parameter)
        {
            var type = string.IsNullOrEmpty(parameter.TypeName) ? "object" : parameter.TypeName;
            var text = $"[{type}] ${parameter.Name}";

            if (!string.IsNullOrEmpty(parameter.DefaultValue)) text += $" = {parameter.DefaultValue}";

            return text;
        }

        /// <summary>Opens a native picker for a path parameter and puts the result in its field.</summary>
        private void BrowseFor(ScriptParameter parameter)
        {
            var picked = parameter.Kind == ScriptParameterKind.File
                ? PickFile(Value(parameter.Name))
                : PickFolder(Value(parameter.Name));

            if (picked is not null) SetValue(parameter.Name, picked);
        }

        private void BrowseLegacyPath()
        {
            if (PickFolder(projectPath) is { } picked) projectPath = picked;
        }

        /// <summary>
        /// Fully qualified rather than a using: System.Windows.Forms has its own Label, Button and
        /// Timer, and importing it into a page is how those start colliding with the framework's.
        /// </summary>
        private string? PickFolder(string current)
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select a folder",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    RootFolder = Environment.SpecialFolder.MyComputer
                };

                if (StartingFolder(current) is { } start) dialog.SelectedPath = start;

                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                error = $"Could not open the folder picker: {ex.Message}";
                return null;
            }
        }

        private string? PickFile(string current)
        {
            try
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Select a file",
                    Filter = "All Files (*.*)|*.*",
                    CheckFileExists = true,
                    CheckPathExists = true,
                    InitialDirectory = StartingFolder(current) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
                };

                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                error = $"Could not open the file picker: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Where a picker should open: whatever is in the field, else the default workspace location
        /// from Settings. Checked for existence because a missing path is silently dropped by one
        /// dialog and honoured as somewhere unrelated by the other.
        /// </summary>
        private string? StartingFolder(string current)
        {
            var candidates = new[]
            {
                Directory.Exists(current) ? current : Path.GetDirectoryName(current),
                defaultWorkspaceLocation
            };

            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));
        }

        /// <summary>
        /// Runs what is in the editor with the values from the form.
        /// <para>
        /// It used to run the text with no parameters at all, which could not work: every bundled
        /// script declares ProjectPath as mandatory, so PowerShell tried to prompt for it and there is
        /// no console here to prompt on. Then it passed exactly one, ProjectPath, from a single box.
        /// </para>
        /// </summary>
        private async Task ExecuteScript()
        {
            if (string.IsNullOrWhiteSpace(scriptText)) return;

            // Stops here rather than at PowerShell's parameter binder, which would report this as an
            // internal-looking error. The notice appears now, and only now.
            showMissingRequired = MissingRequired.Count > 0;
            if (showMissingRequired) return;

            isExecuting = true;
            output = "";
            error = "";

            try
            {
                if (!TryBuildArguments(out var arguments, out var problem))
                {
                    error = problem;
                    return;
                }

                (output, error) = await powerShellService.ExecuteScriptWithParametersAsync(scriptText, arguments);

                if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error))
                {
                    output = "Finished. The script produced no output.";
                }
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

        /// <summary>
        /// Turns the form into arguments for PowerShell, converting each value to the type its
        /// parameter declared.
        /// <para>
        /// An empty optional value is left out entirely rather than passed as "". Passing it would
        /// override the script's own default with nothing, which is a different thing from not
        /// answering — and for a path parameter it is the difference between the script's fallback
        /// and an empty-path error.
        /// </para>
        /// </summary>
        private bool TryBuildArguments(out Dictionary<string, object> arguments, out string problem)
        {
            arguments = new Dictionary<string, object>();
            problem = string.Empty;

            if (parameters.Count == 0)
            {
                // No param block. The service sets unbindable names as variables, so this still
                // reaches a script that reads $ProjectPath directly.
                if (!string.IsNullOrWhiteSpace(projectPath)) arguments["ProjectPath"] = projectPath.Trim();
                return true;
            }

            foreach (var parameter in parameters)
            {
                var raw = Value(parameter.Name);

                if (parameter.Kind == ScriptParameterKind.Switch)
                {
                    // A switch left off is absent, not $false: -Force:$false and no -Force at all
                    // behave the same here, and absent is the honest description.
                    if (IsChecked(parameter.Name)) arguments[parameter.Name] = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (parameter.Kind == ScriptParameterKind.Number)
                {
                    if (!double.TryParse(raw, out var number))
                    {
                        problem = $"{parameter.Label} has to be a number.";
                        return false;
                    }

                    // Whole numbers go over as long, so an [int] parameter binds without PowerShell
                    // having to narrow a double for it.
                    arguments[parameter.Name] = number == Math.Floor(number) && Math.Abs(number) < long.MaxValue
                        ? (long)number
                        : number;
                    continue;
                }

                arguments[parameter.Name] = raw.Trim();
            }

            return true;
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
        
        private Task ValidateCurrentScript()
        {
            if (string.IsNullOrEmpty(scriptText))
                return Task.CompletedTask;
                
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
            
            return Task.CompletedTask;
        }
    }
}