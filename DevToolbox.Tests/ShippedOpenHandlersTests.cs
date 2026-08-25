using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// The shipped openHandlers.yaml, read the way the application reads it.
/// <para>
/// This is here because of what a machine without it does. With no handler for a solution the Open
/// button falls through to the Windows file association, and the association answers with whatever
/// last claimed <c>.sln</c> — on a machine with SQL Server Management Studio installed, that is SSMS,
/// which opened the solution as a text query. So the file existing, parsing, and naming Visual Studio
/// is a behavioural guarantee, not a packaging detail: lose it and a fresh install opens solutions in
/// the wrong program with no error to explain why.
/// </para>
/// </summary>
public class ShippedOpenHandlersTests
{
    /// <summary>The bundled folder as it sits beside the built executable.</summary>
    private static string BundledDirectory => ConfigDefaults.SourceDirectory;

    /// <summary>
    /// Seeds the shipped defaults into a throwaway config folder and reads them back through the
    /// real service, so the assertions cover the YAML parsing and glob matching too — not just that
    /// a file was copied.
    /// </summary>
    private static async Task<IOpenHandlerService> LoadShippedHandlersAsync(string configDirectory)
    {
        Assert.Equal(1, ConfigDefaults.SeedFrom(BundledDirectory, configDirectory));

        var service = new OpenHandlerService(new DirectoryYamlStorage(configDirectory));
        await service.GetConfigAsync();
        return service;
    }

    [Fact]
    public void The_default_config_ships_beside_the_executable()
    {
        // Without this the ConfigDefaults machinery has nothing to seed and does nothing at all,
        // which is indistinguishable from working until someone clicks Open.
        Assert.True(Directory.Exists(BundledDirectory),
            $"No ConfigDefaults folder in the build output ({BundledDirectory}).");
        Assert.True(File.Exists(Path.Combine(BundledDirectory, "openHandlers.yaml")),
            "openHandlers.yaml is not being copied to the build output.");
    }

    [Theory]
    [InlineData(@"C:\tfs\Console Programs\EmployeeEOD\EmployeeEOD.sln")]
    [InlineData(@"C:\tfs\Console Programs\EmployeeEOD\EmployeeEOD.slnf")]
    public async Task A_solution_opens_in_Visual_Studio_rather_than_the_file_association(string path)
    {
        using var config = new TempDirectory();
        var handlers = await LoadShippedHandlersAsync(config.Path);

        var handler = handlers.HandlerFor(path);

        Assert.NotNull(handler);
        Assert.Equal("Visual Studio", handler!.Name);
        Assert.Equal(OpenOptionType.Executable, handler.Type);

        // vswhere, not the registry: App Paths can name an older Visual Studio that cannot read a
        // newer solution and exits without a word.
        Assert.NotNull(handler.ExecutableFrom);
        Assert.Contains("vswhere", handler.ExecutableFrom!.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("devenv", handler.ExecutablePath);
    }

    [Fact]
    public async Task A_workspace_file_opens_in_VS_Code()
    {
        using var config = new TempDirectory();
        var handlers = await LoadShippedHandlersAsync(config.Path);

        var handler = handlers.HandlerFor(@"C:\TFS\Workspaces\dev-checkout.code-workspace");

        Assert.NotNull(handler);
        Assert.Equal("VS Code", handler!.Name);
    }

    [Fact]
    public async Task The_case_of_the_extension_does_not_matter()
    {
        using var config = new TempDirectory();
        var handlers = await LoadShippedHandlersAsync(config.Path);

        Assert.Equal("Visual Studio", handlers.HandlerFor(@"C:\tfs\Thing\Thing.SLN")?.Name);
    }

    [Fact]
    public async Task Files_the_association_already_handles_are_left_alone()
    {
        // Naming an editor this machine may not have would turn a working Open into an error, so
        // the shipped default deliberately claims only the extensions that are actually broken.
        using var config = new TempDirectory();
        var handlers = await LoadShippedHandlersAsync(config.Path);

        Assert.Null(handlers.HandlerFor(@"C:\logs\service.log"));
        Assert.Null(handlers.HandlerFor(@"C:\notes.txt"));
    }

    /// <summary>
    /// Reads YAML out of one directory, the way <see cref="YamlStorageService"/> does, without its
    /// constructor — which points at the real <c>%LOCALAPPDATA%</c> and seeds it.
    /// </summary>
    private sealed class DirectoryYamlStorage : IYamlStorageService
    {
        private static readonly IDeserializer Yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public DirectoryYamlStorage(string directory)
        {
            StorageDirectory = directory;
        }

        public string StorageDirectory { get; }

        public Task<T?> LoadAsync<T>(string fileName)
        {
            var path = Path.Combine(StorageDirectory, $"{fileName}.yaml");
            return Task.FromResult(File.Exists(path) ? Yaml.Deserialize<T>(File.ReadAllText(path)) : default);
        }

        public Task SaveAsync<T>(string fileName, T data) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string fileName) => throw new NotSupportedException();

        public Task<List<string>> ListFilesAsync() => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevToolboxShippedHandlers",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
