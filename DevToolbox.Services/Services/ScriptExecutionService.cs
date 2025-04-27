using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.PowerShell;
using System.Management.Automation;
using System.Text;

namespace DevToolbox.Services.Services;

public class ScriptExecutionService : IScriptExecutionService
{
    private readonly IConfigurationService _configurationService;
    private readonly string _scriptsDirectory;

    public ScriptExecutionService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
        _scriptsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
    }

    public async Task<string> ExecuteScriptAsync(string scriptPath, string filePath)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Script file not found: {scriptPath}");
        }

        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        var scriptType = Path.GetExtension(scriptPath).TrimStart('.');
        
        return await ExecuteScriptAsync(scriptContent, filePath, scriptType);
    }

    public async Task<string> ExecuteScriptAsync(string scriptContent, string filePath, string scriptType)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Target file not found: {filePath}");
        }

        switch (scriptType.ToLower())
        {
            case "ps1":
                return await ExecutePowerShellScriptAsync(scriptContent, filePath);
            case "js":
            case "javascript":
                return await ExecuteJavaScriptAsync(scriptContent, filePath);
            default:
                throw new ArgumentException($"Unsupported script type: {scriptType}");
        }
    }

    public async Task<bool> ValidateScriptAsync(string scriptPath)
    {
        if (!File.Exists(scriptPath))
        {
            return false;
        }

        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        var scriptType = Path.GetExtension(scriptPath).TrimStart('.');
        
        return await ValidateScriptAsync(scriptContent, scriptType);
    }

    public async Task<bool> ValidateScriptAsync(string scriptContent, string scriptType)
    {
        try
        {
            switch (scriptType.ToLower())
            {
                case "ps1":
                    return ValidatePowerShellScript(scriptContent);
                case "js":
                case "javascript":
                    return ValidateJavaScript(scriptContent);
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> ExecutePowerShellScriptAsync(string scriptContent, string filePath)
    {
        using var powershell = PowerShell.Create();
        
        // Add the file path as a parameter
        powershell.AddScript($"$filePath = '{filePath}'");
        powershell.AddScript(scriptContent);

        var results = await powershell.InvokeAsync();
        
        var output = new StringBuilder();
        foreach (var result in results)
        {
            output.AppendLine(result.ToString());
        }

        return output.ToString();
    }

    private async Task<string> ExecuteJavaScriptAsync(string scriptContent, string filePath)
    {
        // TODO: Implement JavaScript execution using a JavaScript engine
        // For now, we'll throw NotImplementedException
        await Task.CompletedTask;
        throw new NotImplementedException("JavaScript execution is not yet implemented");
    }

    private bool ValidatePowerShellScript(string scriptContent)
    {
        try
        {
            using var powershell = PowerShell.Create();
            powershell.AddScript(scriptContent);
            var results = powershell.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ValidateJavaScript(string scriptContent)
    {
        // TODO: Implement JavaScript validation
        // For now, we'll just check if the content is not empty
        return !string.IsNullOrWhiteSpace(scriptContent);
    }

    public async Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters)
    {
        var scriptPath = Path.Combine(_scriptsDirectory, scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Script file not found: {scriptPath}");
        }

        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        using var powershell = PowerShell.Create();
        
        // Add parameters to PowerShell
        foreach (var param in parameters)
        {
            powershell.AddScript($"${param.Key} = '{param.Value}'");
        }
        
        powershell.AddScript(scriptContent);
        await powershell.InvokeAsync();
    }

    public async Task<List<DevToolbox.Services.Models.ScriptInfo>> GetAvailableScriptsAsync()
    {
        var scripts = new List<DevToolbox.Services.Models.ScriptInfo>();
        var files = Directory.GetFiles(_scriptsDirectory, "*.ps1");
        
        foreach (var file in files)
        {
            var scriptContent = await File.ReadAllTextAsync(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var description = ExtractScriptDescription(scriptContent);
            
            scripts.Add(new DevToolbox.Services.Models.ScriptInfo { 
                Name = name,
                FullPath = file,
                LastModified = File.GetLastWriteTime(file)
            });
        }
        
        return scripts;
    }

    private string ExtractScriptDescription(string scriptContent)
    {
        // Look for a comment block at the start of the script
        var lines = scriptContent.Split('\n');
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("#"))
        {
            var description = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("#"))
                {
                    description.AppendLine(line.TrimStart('#').Trim());
                }
                else
                {
                    break;
                }
            }
            return description.ToString().Trim();
        }
        return string.Empty;
    }
} 