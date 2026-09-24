using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// What a workspace card's Run Script menu hands the script it runs.
/// <para>
/// It always passed <c>-ProjectPath &lt;the card's folder&gt;</c>. For a script that declares
/// $ProjectPath that is the whole point; for one that does not, it was an argument the script had
/// never asked for, and any script with a [Parameter()] attribute in its param block rejects an
/// unknown name outright — "A parameter cannot be found that matches parameter name
/// 'ProjectPath'" — so the menu could only run scripts written to its convention.
/// </para>
/// </summary>
public sealed class WorkspaceScriptRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DevToolboxRun_" + Guid.NewGuid().ToString("N"));

    public WorkspaceScriptRunTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class RecordingSystemService : ISystemService
    {
        public Dictionary<string, object>? Parameters { get; private set; }

        public Task<OpenResult> ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters)
        {
            Parameters = parameters;
            return Task.FromResult(OpenResult.Ok());
        }

        public Task<OpenResult> OpenWithCustomAppAsync(string path, CustomOpenOption option, int? line = null) => throw new NotSupportedException();
        public Task<OpenResult> OpenLocationAsync(string path) => throw new NotSupportedException();
        public Task<OpenResult> OpenInExplorerAsync(string path) => throw new NotSupportedException();
        public Task<OpenResult> OpenInTerminalAsync(string path) => throw new NotSupportedException();
        public Task<CommandResult> RunToCompletionAsync(CustomOpenOption o, CancellationToken t = default) => throw new NotSupportedException();
    }

    private sealed class EmptyStorage : IYamlStorageService
    {
        public string StorageDirectory => "none";
        public Task SaveAsync<T>(string fileName, T data) => Task.CompletedTask;
        public Task<T?> LoadAsync<T>(string fileName) => Task.FromResult<T?>(default);
        public Task<bool> DeleteAsync(string fileName) => Task.FromResult(true);
        public Task<List<string>> ListFilesAsync() => Task.FromResult(new List<string>());
    }

    private async Task<Dictionary<string, object>?> RunOnAFolder(string scriptText)
    {
        var scriptPath = Path.Combine(_root, "Script.ps1");
        await File.WriteAllTextAsync(scriptPath, scriptText);

        var folder = Path.Combine(_root, "Project");
        Directory.CreateDirectory(folder);

        var system = new RecordingSystemService();
        var service = new WorkspaceService(new EmptyStorage(), new PowerShellService(), system, new ConfigurationBuilder().Build());

        await service.RunScriptOnLocationAsync(
            new ScriptInfo { Name = "Script", FullPath = scriptPath },
            new Workspace { Id = 1, Name = "Project" },
            new WorkspaceLocation { Name = "dev", Path = folder });

        return system.Parameters;
    }

    [Fact]
    public async Task A_script_that_declares_ProjectPath_is_given_the_cards_folder()
    {
        var parameters = await RunOnAFolder("param([Parameter(Mandatory = $true)][string]$ProjectPath)");

        Assert.Equal(Path.Combine(_root, "Project"), parameters!["ProjectPath"]);
    }

    [Fact]
    public async Task The_name_is_matched_the_way_PowerShell_matches_it()
    {
        var parameters = await RunOnAFolder("param([string]$projectpath)");

        Assert.True(parameters!.ContainsKey("ProjectPath"));
    }

    [Fact]
    public async Task A_script_that_does_not_declare_it_is_not_sent_it()
    {
        var parameters = await RunOnAFolder("[CmdletBinding()] param([string]$OutputFile = 'out.txt')");

        Assert.Empty(parameters!);
    }

    [Fact]
    public async Task A_script_with_no_param_block_is_sent_nothing()
    {
        var parameters = await RunOnAFolder("Write-Host 'no parameters'");

        Assert.Empty(parameters!);
    }
}
