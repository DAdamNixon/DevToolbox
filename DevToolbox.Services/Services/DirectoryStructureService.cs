using System.Text.Json;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

public class DirectoryStructureService
{
    private readonly IScriptExecutionService _scriptService;
    private readonly string _scriptsDirectory;

    public DirectoryStructureService(IScriptExecutionService scriptService)
    {
        _scriptService = scriptService;
        _scriptsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
    }

    public async Task<DirectoryStructure> GetDirectoryStructureAsync(
        string directory,
        string filePattern = "*.*",
        bool includeDirectories = false,
        bool includeHidden = false,
        bool includeSystem = false)
    {
        var scriptPath = Path.Combine(_scriptsDirectory, "GetDirectoryStructure.ps1");
        
        // Build PowerShell parameters
        var parameters = new Dictionary<string, object>
        {
            { "Directory", directory },
            { "FilePattern", filePattern },
            { "IncludeDirectories", includeDirectories },
            { "IncludeHidden", includeHidden },
            { "IncludeSystem", includeSystem }
        };

        // Execute the script
        var result = await _scriptService.ExecuteScriptAsync(scriptPath, directory);
        
        // Parse the JSON result
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var structure = JsonSerializer.Deserialize<DirectoryStructure>(result, options);
        if (structure == null)
        {
            throw new InvalidOperationException("Failed to parse directory structure");
        }

        return structure;
    }

    public async Task<IEnumerable<Models.FileInfo>> FindFilesAsync(
        string directory,
        string pattern,
        bool includeHidden = false,
        bool includeSystem = false)
    {
        var structure = await GetDirectoryStructureAsync(
            directory,
            pattern,
            includeDirectories: false,
            includeHidden,
            includeSystem
        );

        return GetAllFiles(structure.Structure);
    }

    private IEnumerable<Models.FileInfo> GetAllFiles(DirectoryNode node)
    {
        foreach (var file in node.Files)
        {
            yield return file;
        }

        foreach (var dir in node.Directories)
        {
            foreach (var file in GetAllFiles(dir))
            {
                yield return file;
            }
        }
    }
} 