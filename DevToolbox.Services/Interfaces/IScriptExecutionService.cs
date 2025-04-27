using DevToolbox.Services.Models;
using System.Threading.Tasks;

namespace DevToolbox.Services.Interfaces;

public interface IScriptExecutionService
{
    Task<string> ExecuteScriptAsync(string scriptPath, string filePath);
    Task<string> ExecuteScriptAsync(string scriptContent, string filePath, string scriptType);
    Task<bool> ValidateScriptAsync(string scriptPath);
    Task<bool> ValidateScriptAsync(string scriptContent, string scriptType);
    Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters);
    Task<List<ScriptInfo>> GetAvailableScriptsAsync();
} 