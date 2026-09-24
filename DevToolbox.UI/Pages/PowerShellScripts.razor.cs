using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Models;
using DevToolbox.UI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DevToolbox.UI.Pages
{
    public partial class PowerShellScripts : ComponentBase, IDisposable
    {
        private List<ScriptInfo> availableScripts = new();
        private string selectedScript = "";
        private string scriptText = "";

        /// <summary>
        /// The text as it is on disk, for "Unsaved changes" on the folded editor — which is the one
        /// place an edit could otherwise go unnoticed, since the editor showing it is out of sight.
        /// </summary>
        private string savedScriptText = "";

        /// <summary>
        /// Typed into since the editor last handed its text back. The text itself only arrives when
        /// the editor loses focus, so without this Save would not appear until you had clicked away
        /// from the thing you just changed.
        /// </summary>
        private bool editedSinceSync;

        /// <summary>
        /// There is something to save — what drives the Save button and the folded editor's
        /// "Unsaved changes". Compared with the file rather than remembered as a flag once the text
        /// is in, so undoing an edit back to what is on disk takes Save away again.
        /// </summary>
        private bool IsDirty => editedSinceSync || Lf(scriptText) != Lf(savedScriptText);

        /// <summary>
        /// Line endings out of the comparison. A textarea hands back \n whatever it was given, and
        /// every script on disk here is \r\n, so compared as-is a script was "changed" the first
        /// time the editor gave its text back — Save up, and staying up, with nothing to save.
        /// </summary>
        private static string Lf(string text) => text.Replace("\r\n", "\n");

        private void OnScriptEdited() => editedSinceSync = true;

        private void OnScriptChanged()
        {
            editedSinceSync = false;
            SyncParameters();
        }

        // --- deleting ---

        /// <summary>
        /// The first click on Delete has happened. The second deletes; clicking anywhere else
        /// (the button losing focus) stands it down. Two steps because the file goes now, unlike
        /// the Smart Folders Remove whose look this borrows, which is undone by closing the dialog.
        /// </summary>
        private bool confirmingDelete;

        private async Task DeleteClicked()
        {
            if (!confirmingDelete)
            {
                confirmingDelete = true;
                return;
            }

            confirmingDelete = false;
            await DeleteScript();
        }

        private void DisarmDelete() => confirmingDelete = false;

        /// <summary>The folded editor's one line: how big the script is and what it asks for.</summary>
        private string EditorSummary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(scriptText)) return "Empty script";

                var lines = scriptText.TrimEnd('\r', '\n').Split('\n').Length;
                var text = $"{lines:N0} line{(lines == 1 ? "" : "s")}";
                if (parameters.Count > 0) text += $" · {parameters.Count} parameter{(parameters.Count == 1 ? "" : "s")}";
                return text;
            }
        }

        private void ToggleEditor() => Run.EditorCollapsed = !Run.EditorCollapsed;

        /// <summary>
        /// A script picked from the list. Unfolds the editor, because picking a script is asking to
        /// see it — unlike the load that happens on arriving at the tab, which leaves it folded.
        /// </summary>
        private async Task SelectScript(string name)
        {
            Run.EditorCollapsed = false;
            await LoadScript(name);
        }

        /// <summary>
        /// A one-line result beside the buttons: saved, deleted, validated, or why not.
        /// <para>
        /// These used to be written into the script output pane, so "Script saved successfully"
        /// sat in the same green box a run's output did, and a failed save in the same red one as a
        /// script's errors. The console is for what the script says now; this is for what the tab did.
        /// </para>
        /// </summary>
        private string statusMessage = "";
        private bool statusIsError;

        private void ShowStatus(string message, bool isError = false)
        {
            statusMessage = message;
            statusIsError = isError;
        }

        // --- the console ---

        /// <summary>The body of the console, for the scroll-follow script.</summary>
        private ElementReference consoleBody;

        /// <summary>
        /// scriptConsole.attach has run on the console currently in the DOM. Reset whenever the
        /// console is not rendered, because the next one is a new element.
        /// </summary>
        private bool consoleAttached;

        /// <summary>Run was just pressed: bring the console on screen after the next render.</summary>
        private bool revealConsole;

        /// <summary>The console is showing only the error stream.</summary>
        private bool errorsOnly;

        /// <summary>
        /// Lines drawn at once. A run that writes more still keeps all of it, and Copy takes all of
        /// it; only the oldest stop being drawn, because a few hundred thousand divs re-diffed ten
        /// times a second would make the tab unusable in the middle of the run you are watching.
        /// </summary>
        private const int MaxRenderedLines = 2000;

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
        [Inject] ScriptRunSession Run { get; set; } = null!;
        [Inject] IJSRuntime JS { get; set; } = null!;
        private string searchText = "";

        protected override async Task OnInitializedAsync()
        {
            Run.OnChanged += HandleRunChanged;

            // Coming back to the tab mid-run: the run is what you came back for, so it is brought
            // into view as if Run had just been pressed. A finished one is left where it is.
            revealConsole = Run.IsRunning;

            defaultWorkspaceLocation = (await uiSettings.GetAsync()).DefaultWorkspaceLocation;
            await LoadScripts();
        }

        private async void HandleRunChanged() => await InvokeAsync(StateHasChanged);

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!Run.HasRun)
            {
                consoleAttached = false;
                return;
            }

            if (!consoleAttached)
            {
                await JS.InvokeVoidAsync("scriptConsole.attach", consoleBody);
                consoleAttached = true;
            }

            if (revealConsole)
            {
                revealConsole = false;
                await JS.InvokeVoidAsync("scriptConsole.reveal", consoleBody);
            }
        }

        public void Dispose() => Run.OnChanged -= HandleRunChanged;
        
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
                ShowStatus($"Could not load script '{name}'.", isError: true);
            }
            savedScriptText = scriptText;
            editedSinceSync = false;
            confirmingDelete = false;

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

            // The file's own line endings, not the textarea's. It hands back \n regardless, so
            // saving used to turn every \r\n script into a \n one and a one-word fix into a
            // whole-file change.
            var content = savedScriptText.Contains("\r\n")
                ? Lf(scriptText).Replace("\n", "\r\n")
                : scriptText;

            var result = await powerShellService.SaveScriptAsync(selectedScript, content, enableScriptValidation);

            if (result.Success)
            {
                savedScriptText = content;
                editedSinceSync = false;
                await LoadScripts();
                ShowStatus($"Saved {selectedScript}.");

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
                ShowStatus($"Could not save {selectedScript}. {result.ErrorMessage}", isError: true);

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
                ShowStatus($"Deleted {deleted}.");
            }
            else
            {
                ShowStatus($"Could not delete {selectedScript}.", isError: true);
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
                ShowStatus($"Could not create the script. {result.ErrorMessage}", isError: true);
                return;
            }

            selectedScript = name;
            scriptText = templateContent;
            savedScriptText = templateContent;
            editedSinceSync = false;
            Run.EditorCollapsed = false;
            SyncParameters();
            ClearValidation();

            await LoadScripts();
            ShowStatus($"Created {name}.");
        }

        /// <summary>
        /// The fallback body, for a build with no bundled ScriptTemplate.ps1.
        /// <para>
        /// It used to open with a mandatory $ProjectPath, so every new script demanded a folder
        /// before it would run, whether or not it had any use for one. Now it asks for nothing,
        /// and says how to ask: each parameter becomes a field, and declaring $ProjectPath is what
        /// puts a script on the workspace cards' Run Script menu.
        /// </para>
        /// </summary>
        private static string DefaultTemplate() =>
            """
            # What this script does, in a line or two.
            #
            # Parameters go in param() below, and each one becomes a field on the Scripts tab. None is
            # required unless it is marked [Parameter(Mandatory)]. Declare one named $ProjectPath and
            # this script also appears in every project card's Run Script menu, run on that card's folder.
            param()

            # Write-Host shows in the Scripts tab's console as the script runs.
            Write-Host "Hello from DevToolbox."
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
                ShowStatus($"Could not open the folder picker: {ex.Message}", isError: true);
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
                ShowStatus($"Could not open the file picker: {ex.Message}", isError: true);
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

            if (!TryBuildArguments(out var arguments, out var problem))
            {
                ShowStatus(problem, isError: true);
                return;
            }

            ShowStatus("");
            errorsOnly = false;
            revealConsole = true;

            // Folds the editor so the console gets the column, as the Log Viewer folds its source
            // card once a search starts: you have finished writing and started watching. The lip
            // brings it straight back.
            Run.EditorCollapsed = true;

            // Returns when the script ends. The console redraws throughout, from Run.OnChanged.
            await Run.RunAsync(selectedScript, scriptText, arguments);
        }

        private void StopScript() => Run.Stop();

        private void ClearOutput()
        {
            errorsOnly = false;
            Run.Clear();
        }

        private async Task CopyOutput()
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", Run.ToPlainText());
            ShowStatus($"Copied {Run.Lines.Count:N0} line{(Run.Lines.Count == 1 ? "" : "s")} of output.");
        }

        private void ToggleErrorsOnly() => errorsOnly = !errorsOnly;

        /// <summary>What the console draws: the error stream alone, or the newest lines of everything.</summary>
        private IEnumerable<ScriptOutputLine> VisibleLines =>
            errorsOnly
                ? Run.Lines.Where(l => l.Kind == ScriptOutputKind.Error)
                : Run.Lines.Skip(Math.Max(0, Run.Lines.Count - MaxRenderedLines));

        private int HiddenLineCount => errorsOnly ? 0 : Math.Max(0, Run.Lines.Count - MaxRenderedLines);

        /// <summary>
        /// The stream decides the marker and tint, so an error is recognisable by more than its
        /// colour; Write-Host's own colour, where the script chose one, decides the text.
        /// </summary>
        private static string LineClass(ScriptOutputLine line)
        {
            var kind = line.Kind switch
            {
                ScriptOutputKind.Error => "is-error",
                ScriptOutputKind.Warning => "is-warning",
                ScriptOutputKind.NativeStderr => "is-stderr",
                ScriptOutputKind.Verbose => "is-verbose",
                _ => ""
            };

            var color = line.Color switch
            {
                ConsoleColor.Cyan or ConsoleColor.DarkCyan => "c-cyan",
                ConsoleColor.Green or ConsoleColor.DarkGreen => "c-green",
                ConsoleColor.Yellow or ConsoleColor.DarkYellow => "c-yellow",
                ConsoleColor.Red or ConsoleColor.DarkRed => "c-red",
                ConsoleColor.Blue or ConsoleColor.DarkBlue => "c-blue",
                ConsoleColor.Magenta or ConsoleColor.DarkMagenta => "c-magenta",
                ConsoleColor.DarkGray => "c-muted",
                // Gray and White are what a console prints by default, and Black on this ground
                // would be invisible, so all three are the ordinary text colour.
                _ => ""
            };

            return $"ps-line {kind} {color}".TrimEnd();
        }

        private (string Icon, string Text, string Tone) ConsoleStatus()
        {
            if (Run.IsRunning)
            {
                return Run.IsStopping
                    ? ("bi-hourglass-split", "Stopping", "is-stopped")
                    : ("bi-arrow-repeat ws-spin", "Running", "is-running");
            }

            return Run.Outcome switch
            {
                ScriptRunOutcome.Failed => ("bi-x-circle", "Failed", "is-failed"),
                ScriptRunOutcome.Stopped => ("bi-stop-circle", "Stopped", "is-stopped"),
                _ when Run.ErrorCount > 0 => ("bi-exclamation-circle", $"Finished with {Run.ErrorCount:N0} error{(Run.ErrorCount == 1 ? "" : "s")}", "is-failed"),
                _ => ("bi-check-circle", "Finished", "is-ok")
            };
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");

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
                ShowStatus("Validation passed.");
            }
            
            return Task.CompletedTask;
        }
    }
}