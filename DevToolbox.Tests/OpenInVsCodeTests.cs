using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// What "Open in VS Code" actually hands to VS Code.
/// <para>
/// The rule is not "the location's path": most locations on this dashboard point at a
/// <c>.sln</c> or a <c>.slnf</c>, and opening one of those in VS Code shows you its XML in an
/// editor tab, which is never what the menu item is being asked for. The folder around it is the
/// project. A <c>.code-workspace</c> is the exception, because it *is* a workspace.
/// </para>
/// </summary>
public class OpenInVsCodeTests : IDisposable
{
    private readonly string _root;

    public OpenInVsCodeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DevToolboxCode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not worth failing a test run over.
        }
    }

    /// <summary>Records what it was asked to open instead of launching anything.</summary>
    private sealed class RecordingSystemService : ISystemService
    {
        public string? OpenedPath { get; private set; }
        public CustomOpenOption? OpenedWith { get; private set; }

        public Task<OpenResult> OpenWithCustomAppAsync(string path, CustomOpenOption option, int? line = null)
        {
            OpenedPath = path;
            OpenedWith = option;
            return Task.FromResult(OpenResult.Ok());
        }

        public Task<OpenResult> OpenLocationAsync(string path) => throw new NotSupportedException();
        public Task<OpenResult> OpenInExplorerAsync(string path) => throw new NotSupportedException();
        public Task<OpenResult> OpenInTerminalAsync(string path) => throw new NotSupportedException();
        public Task<OpenResult> ExecuteScriptAsync(string s, Dictionary<string, object> p) => throw new NotSupportedException();
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

    private (WorkspaceService Service, RecordingSystemService System) NewService()
    {
        var system = new RecordingSystemService();
        var configuration = new ConfigurationBuilder().Build();

        return (new WorkspaceService(new EmptyStorage(), new PowerShellService(), system, configuration), system);
    }

    private WorkspaceLocation At(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "{}");

        // Root deliberately left blank: it is empty on any location added by hand rather than
        // produced by a scan, and reading it instead of Path is the bug RunScriptOnLocationAsync
        // documents.
        return new WorkspaceLocation { Name = "dev", Path = full };
    }

    [Fact]
    public async Task A_solution_opens_the_folder_around_it()
    {
        var (service, system) = NewService();
        var location = At(@"Checkout\Checkout.sln");

        await service.OpenLocationInVsCodeAsync(location);

        Assert.Equal(Path.Combine(_root, "Checkout"), system.OpenedPath);
    }

    [Fact]
    public async Task A_solution_filter_opens_the_folder_too()
    {
        var (service, system) = NewService();
        var location = At(@"Account\Account.Development.slnf");

        await service.OpenLocationInVsCodeAsync(location);

        Assert.Equal(Path.Combine(_root, "Account"), system.OpenedPath);
    }

    [Fact]
    public async Task A_code_workspace_opens_its_folder_too()
    {
        var (service, system) = NewService();
        var location = At(@"Bench\bench.code-workspace");

        await service.OpenLocationInVsCodeAsync(location);

        // No exception for .code-workspace: the rule is the folder, never the file. Opening the
        // workspace file itself would land you in a JSON tab.
        Assert.Equal(Path.Combine(_root, "Bench"), system.OpenedPath);
    }

    [Fact]
    public async Task A_folder_opens_as_itself()
    {
        var (service, system) = NewService();
        var folder = Path.Combine(_root, "SomeRepo");
        Directory.CreateDirectory(folder);

        await service.OpenLocationInVsCodeAsync(new WorkspaceLocation
        {
            Name = "repo",
            Path = folder,
            Type = LocationType.Folder
        });

        Assert.Equal(folder, system.OpenedPath);
    }

    [Fact]
    public async Task It_asks_for_code_by_name_rather_than_by_install_path()
    {
        var (service, system) = NewService();

        await service.OpenLocationInVsCodeAsync(At(@"Checkout\Checkout.sln"));

        // `code` on PATH is code.cmd, which ISystemService already resolves across PATH x PATHEXT
        // and then the App Paths registry — the same route openHandlers.yaml uses. A hardcoded
        // install path would be wrong on every machine that put VS Code somewhere else.
        Assert.Equal("code", system.OpenedWith?.ExecutablePath);
        Assert.Equal(OpenOptionType.Executable, system.OpenedWith?.Type);
    }

    [Fact]
    public async Task A_path_that_no_longer_exists_says_so_instead_of_launching_anything()
    {
        var (service, system) = NewService();

        var result = await service.OpenLocationInVsCodeAsync(new WorkspaceLocation
        {
            Name = "gone",
            Path = Path.Combine(_root, "Deleted", "Gone.sln")
        });

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
        Assert.Null(system.OpenedPath);
    }

    [Fact]
    public async Task A_blank_path_is_refused_rather_than_opening_the_current_directory()
    {
        var (service, system) = NewService();

        var result = await service.OpenLocationInVsCodeAsync(new WorkspaceLocation { Name = "empty", Path = "" });

        Assert.False(result.Success);
        Assert.Null(system.OpenedPath);
    }
}
