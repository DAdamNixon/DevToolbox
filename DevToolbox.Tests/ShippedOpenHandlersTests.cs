using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

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
        using var config = new TempDirectory("DevToolboxShippedHandlers");
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

    [Theory]
    [InlineData(@"C:\tfs\Console Programs\EmployeeEOD\EmployeeEOD.sln")]
    [InlineData(@"C:\tfs\Console Programs\EmployeeEOD\EmployeeEOD.slnf")]
    public async Task The_locator_asks_for_devenv_by_name_and_not_for_the_newest_of_any_product(string path)
    {
        // The bug this guards. `-latest -products * -property productPath` reads as "the newest
        // Visual Studio" and is not: SQL Server Management Studio is built on the VS shell and
        // installs through the VS Installer, so it registers as a product too. On a machine with
        // SSMS 21 it outranked Visual Studio and every solution opened in SSMS as a text query —
        // with no error, because the handler did exactly what it was told.
        //
        // Asking for devenv.exe by name cannot pick SSMS, which ships Ssms.exe and has no
        // devenv.exe to find, however it chooses to register itself.
        using var config = new TempDirectory("DevToolboxShippedHandlers");
        var handlers = await LoadShippedHandlersAsync(config.Path);

        var arguments = handlers.HandlerFor(path)!.ExecutableFrom!.Arguments;

        Assert.Contains(@"-find **\devenv.exe", arguments);
        Assert.DoesNotContain("-property productPath", arguments);
        Assert.DoesNotContain("-latest", arguments);
    }

    [Fact]
    public async Task A_workspace_file_opens_in_VS_Code()
    {
        using var config = new TempDirectory("DevToolboxShippedHandlers");
        var handlers = await LoadShippedHandlersAsync(config.Path);

        var handler = handlers.HandlerFor(@"C:\TFS\Workspaces\dev-checkout.code-workspace");

        Assert.NotNull(handler);
        Assert.Equal("VS Code", handler!.Name);
    }

    [Fact]
    public async Task The_case_of_the_extension_does_not_matter()
    {
        using var config = new TempDirectory("DevToolboxShippedHandlers");
        var handlers = await LoadShippedHandlersAsync(config.Path);

        Assert.Equal("Visual Studio", handlers.HandlerFor(@"C:\tfs\Thing\Thing.SLN")?.Name);
    }

    [Theory]
    [InlineData(@"C:\logs\service.log")]
    [InlineData(@"C:\notes.txt")]
    public async Task Log_and_text_files_open_at_the_line_the_Log_Viewer_was_on(string path)
    {
        // These carry the editor's jump-to-line switch, which is why they are handlers rather than
        // being left to Windows: the Log Viewer passes the double-clicked row as {1}.
        using var config = new TempDirectory("DevToolboxShippedHandlers");
        var handlers = await LoadShippedHandlersAsync(config.Path);

        var handler = handlers.HandlerFor(path);

        Assert.NotNull(handler);
        Assert.Equal("VS Code", handler!.Name);
        Assert.Contains("{1}", handler.Arguments);
    }
}
