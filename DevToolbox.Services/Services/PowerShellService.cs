using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Services.Services;

public class PowerShellService
{
    private readonly string _scriptsDirectory;

    /// <summary>
    /// UTF-8 <em>with</em> a byte order mark. Windows PowerShell reads a .ps1 that has no BOM as ANSI,
    /// so an em dash or a smart quote in a comment becomes three characters, one of which can close a
    /// string and break parsing. Saving without one has already cost this project an afternoon.
    /// </summary>
    private static readonly UTF8Encoding ScriptEncoding = new(encoderShouldEmitUTF8Identifier: true);
    
    public PowerShellService()
    {
        // Under %LOCALAPPDATA%, not beside the executable: this service saves and deletes, and an
        // installed package's own folder is read-only. See ScriptLibrary.
        _scriptsDirectory = ScriptLibrary.EnsureUserDirectory();
    }
    
    /// <summary>
    /// Gets the path to the scripts directory
    /// </summary>
    public string ScriptsDirectory => _scriptsDirectory;
    
    /// <summary>
    /// Gets a list of available script files
    /// </summary>
    public IEnumerable<DevToolbox.Services.Models.ScriptInfo> GetAvailableScripts()
    {
        if (!Directory.Exists(_scriptsDirectory))
        {
            yield break;
        }
        
        foreach (var file in Directory.GetFiles(_scriptsDirectory, "*.ps1"))
        {
            yield return new DevToolbox.Services.Models.ScriptInfo
            {
                Name = Path.GetFileNameWithoutExtension(file),
                FullPath = file,
                LastModified = File.GetLastWriteTime(file)
            };
        }
    }
    
    /// <summary>
    /// Executes a script file from the Scripts directory
    /// </summary>
    /// <param name="scriptName">The name of the script file (without extension)</param>
    /// <param name="parameters">Optional parameters to pass to the script</param>
    /// <returns>The script output and any errors</returns>
    public async Task<(string Output, string Error)> ExecuteScriptFileAsync(string scriptName, Dictionary<string, object>? parameters = null)
    {
        string scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.ps1");
        
        if (!File.Exists(scriptPath))
        {
            return (string.Empty, $"Script file '{scriptName}.ps1' not found.");
        }
        
        string scriptText = await File.ReadAllTextAsync(scriptPath);
        return await ExecuteScriptWithParametersAsync(scriptText, parameters);
    }
    
    /// <summary>
    /// The parameters a script insists on being given. Exposed so the editor can say which values a
    /// script needs before you press Run, rather than only afterwards.
    /// </summary>
    public static IReadOnlyList<string> RequiredParameters(string? scriptText)
    {
        if (string.IsNullOrWhiteSpace(scriptText)) return Array.Empty<string>();

        var ast = Parser.ParseInput(scriptText, out _, out var errors);
        if (errors.Length > 0 || ast.ParamBlock is null) return Array.Empty<string>();

        return ast.ParamBlock.Parameters
            .Where(IsMandatory)
            .Select(p => p.Name.VariablePath.UserPath)
            .ToList();
    }

    /// <summary>
    /// Executes a PowerShell script with parameters and returns the results as a string.
    /// </summary>
    /// <param name="scriptText">The PowerShell script to execute</param>
    /// <param name="parameters">Optional parameters to pass to the script</param>
    /// <returns>The script output and any errors</returns>
    public async Task<(string Output, string Error)> ExecuteScriptWithParametersAsync(string scriptText, Dictionary<string, object>? parameters = null)
    {
        // Parsed before it is run, for two reasons: a syntax error becomes a message here instead of a
        // silent nothing, and the parameters a script declares are the only ones that can be bound to
        // it - passing one it does not declare is an error rather than a no-op.
        var ast = Parser.ParseInput(scriptText, out _, out var parseErrors);
        if (parseErrors.Length > 0)
        {
            return (string.Empty, string.Join(Environment.NewLine, parseErrors.Select(e => e.ToString())));
        }

        var declared = ast.ParamBlock?.Parameters ?? (IReadOnlyList<ParameterAst>)Array.Empty<ParameterAst>();
        var supplied = parameters ?? new Dictionary<string, object>();

        // A mandatory parameter with nothing bound to it makes PowerShell prompt, and there is no
        // console here to prompt on: the run fails with a binding error that reads like an internal
        // fault. Every script bundled with DevToolbox declares ProjectPath as mandatory, so this was
        // the whole of "the Run button does nothing". Naming the missing value is the difference
        // between "this is broken" and "this needs a path".
        var missing = declared
            .Where(IsMandatory)
            .Select(p => p.Name.VariablePath.UserPath)
            .Where(name => !supplied.ContainsKey(name))
            .ToList();

        if (missing.Count > 0)
        {
            return (string.Empty,
                $"This script needs a value for {string.Join(", ", missing)}. " +
                "Enter one in the path box and run it again.");
        }

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        // An explicit runspace, so SessionStateProxy exists before the first invoke.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(scriptText);

        var declaredNames = declared
            .Select(p => p.Name.VariablePath.UserPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in supplied)
        {
            if (declaredNames.Contains(parameter.Key))
            {
                ps.AddParameter(parameter.Key, parameter.Value);
            }
            else
            {
                // Undeclared, so it cannot be bound - but a script may still read $ProjectPath
                // directly. Setting a variable is what the old code did for *every* parameter, via a
                // second AddScript, which is why nothing with a param block ever received one: two
                // AddScript calls build a pipeline, not a preamble.
                runspace.SessionStateProxy.SetVariable(parameter.Key, parameter.Value);
            }
        }

        ps.Streams.Error.DataAdded += (sender, e) =>
            errorBuilder.AppendLine(((PSDataCollection<ErrorRecord>)sender!)[e.Index].ToString());

        ps.Streams.Warning.DataAdded += (sender, e) =>
            outputBuilder.AppendLine("WARNING: " + ((PSDataCollection<WarningRecord>)sender!)[e.Index].Message);

        // Write-Host goes to the information stream, not the pipeline. Every bundled script reports
        // its progress with Write-Host, so without this one a script could do its entire job and
        // still show an empty output pane.
        ps.Streams.Information.DataAdded += (sender, e) =>
        {
            var message = ((PSDataCollection<InformationRecord>)sender!)[e.Index].MessageData?.ToString();
            if (!string.IsNullOrEmpty(message)) outputBuilder.AppendLine(message);
        };

        try
        {
            foreach (var item in await ps.InvokeAsync())
            {
                outputBuilder.AppendLine(item?.ToString());
            }
        }
        catch (RuntimeException ex)
        {
            // A terminating error never reaches the error stream, so without this the run ends with
            // empty output and no explanation at all.
            errorBuilder.AppendLine(ex.Message);
        }

        return (outputBuilder.ToString(), errorBuilder.ToString());
    }

    /// <summary>
    /// Whether a declared parameter has to be supplied: <c>[Parameter(Mandatory=$true)]</c>, or the
    /// <c>[Parameter(Mandatory)]</c> shorthand.
    /// </summary>
    private static bool IsMandatory(ParameterAst parameter) =>
        parameter.Attributes
            .OfType<AttributeAst>()
            .Where(a => a.TypeName.GetReflectionAttributeType() == typeof(ParameterAttribute))
            .SelectMany(a => a.NamedArguments)
            .Any(argument =>
                argument.ArgumentName.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) &&
                (argument.ExpressionOmitted ||
                 argument.Argument is VariableExpressionAst { VariablePath.UserPath: "true" }));
    
    /// <summary>
    /// Executes a PowerShell script and returns the results as a string.
    /// </summary>
    /// <param name="scriptText">The PowerShell script to execute</param>
    /// <returns>The script output and any errors</returns>
    public async Task<(string Output, string Error)> ExecuteScriptAsync(string scriptText)
    {
        return await ExecuteScriptWithParametersAsync(scriptText, null);
    }
    
    /// <summary>
    /// Executes a PowerShell command and returns the results.
    /// </summary>
    /// <param name="command">The PowerShell command to execute</param>
    /// <param name="parameters">Optional parameters for the command</param>
    /// <returns>The command output and any errors</returns>
    public async Task<(string Output, string Error)> ExecuteCommandAsync(string command, Dictionary<string, object>? parameters = null)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        
        using var ps = PowerShell.Create();
        
        // Create the command
        var cmd = ps.AddCommand(command);
        
        // Add parameters if provided
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                cmd.AddParameter(param.Key, param.Value);
            }
        }
        
        // Configure output handling
        ps.Streams.Error.DataAdded += (object sender, DataAddedEventArgs e) =>
        {
            var error = ((PSDataCollection<ErrorRecord>)sender)[e.Index];
            errorBuilder.AppendLine(error.ToString());
        };
        
        // Execute the command
        var results = await ps.InvokeAsync();
        
        // Process the results
        foreach (var item in results)
        {
            outputBuilder.AppendLine(item.ToString());
        }
        
        return (outputBuilder.ToString(), errorBuilder.ToString());
    }
    
    /// <summary>
    /// Saves a script to the Scripts directory
    /// </summary>
    /// <param name="scriptName">The name of the script (without extension)</param>
    /// <param name="scriptContent">The content of the script</param>
    /// <param name="validateScript">Whether to validate the script structure before saving</param>
    /// <returns>Result of the save operation with validation details if applicable</returns>
    public async Task<ScriptSaveResult> SaveScriptAsync(string scriptName, string scriptContent, bool validateScript = false)
    {
        var result = new ScriptSaveResult { Success = false };
        
        try
        {
            // Validate script structure if requested
            if (validateScript)
            {
                var validator = new ScriptValidationService();
                var validationResult = validator.ValidateScript(scriptContent);
                
                result.ValidationResult = validationResult;
                
                // If script has errors, don't save it
                if (!validationResult.IsValid)
                {
                    result.ErrorMessage = "Script contains validation errors and was not saved.";
                    return result;
                }
                
                // If script has warnings, note them but still save
                if (validationResult.HasWarnings)
                {
                    result.WarningMessage = "Script was saved but has validation warnings.";
                }
            }
            
            string scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent, ScriptEncoding);
            
            result.Success = true;
            result.ScriptPath = scriptPath;
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }
    
    /// <summary>
    /// Deletes a script from the Scripts directory
    /// </summary>
    /// <param name="scriptName">The name of the script (without extension)</param>
    /// <returns>True if successful</returns>
    public bool DeleteScript(string scriptName)
    {
        try
        {
            string scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.ps1");
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    /// <summary>
    /// Gets the content of a script
    /// </summary>
    /// <param name="scriptName">The name of the script (without extension)</param>
    /// <returns>The script content or null if the script doesn't exist</returns>
    public async Task<string?> GetScriptContentAsync(string scriptName)
    {
        string scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.ps1");
        if (File.Exists(scriptPath))
        {
            return await File.ReadAllTextAsync(scriptPath);
        }
        return null;
    }
} 