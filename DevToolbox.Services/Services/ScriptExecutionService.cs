using DevToolbox.Services.Interfaces;
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
} 