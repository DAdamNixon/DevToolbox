using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Reflection;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

public class PowerShellService
{
    private readonly string _scriptsDirectory;
    
    public PowerShellService()
    {
        // Get the base directory of the application
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        
        // Determine the location of scripts - defaults to Scripts folder in the same directory as the assembly
        _scriptsDirectory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? baseDirectory, "Scripts");
        
        // Create the directory if it doesn't exist
        if (!Directory.Exists(_scriptsDirectory))
        {
            Directory.CreateDirectory(_scriptsDirectory);
        }
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
    /// Executes a PowerShell script with parameters and returns the results as a string.
    /// </summary>
    /// <param name="scriptText">The PowerShell script to execute</param>
    /// <param name="parameters">Optional parameters to pass to the script</param>
    /// <returns>The script output and any errors</returns>
    public async Task<(string Output, string Error)> ExecuteScriptWithParametersAsync(string scriptText, Dictionary<string, object>? parameters = null)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        
        using var ps = PowerShell.Create();
        
        // Add the script to the PowerShell object
        ps.AddScript(scriptText);
        
        // Add parameters if provided
        if (parameters != null && parameters.Count > 0)
        {
            ps.AddParameters(parameters);
        }
        
        // Configure output handling
        ps.Streams.Error.DataAdded += (object sender, DataAddedEventArgs e) =>
        {
            var error = ((PSDataCollection<ErrorRecord>)sender)[e.Index];
            errorBuilder.AppendLine(error.ToString());
        };
        
        // Execute the script
        var results = await ps.InvokeAsync();
        
        // Process the results
        foreach (var item in results)
        {
            outputBuilder.AppendLine(item.ToString());
        }
        
        return (outputBuilder.ToString(), errorBuilder.ToString());
    }
    
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
    /// <returns>True if successful</returns>
    public async Task<bool> SaveScriptAsync(string scriptName, string scriptContent)
    {
        try
        {
            string scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent);
            return true;
        }
        catch (Exception)
        {
            return false;
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