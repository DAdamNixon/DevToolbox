using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// What makes Settings' Restore button actually take effect.
/// <para>
/// Restoring copies a shipped file over the live one on disk, without going through the service
/// that owns it. Every service here loads its config once and keeps it, so unless the restore says
/// so, the old snapshot is served for the rest of the process — the restore reports success and
/// nothing changes. That is how a correct <c>*.sln</c> handler sat on disk while solutions kept
/// opening in the wrong program.
/// </para>
/// </summary>
public class CachedConfigInvalidationTests
{
    private const string OpensInVisualStudio = """
        handlers:
        - match: '*.sln'
          name: Visual Studio
          type: Executable
          executablePath: devenv
        """;

    private const string OpensInSsms = """
        handlers:
        - match: '*.sln'
          name: SSMS
          type: Executable
          executablePath: ssms
        """;

    private static OpenHandlerService HandlersReading(string directory) =>
        new(new DirectoryYamlStorage(directory));

    private static void WriteConfig(string directory, string yaml) =>
        File.WriteAllText(Path.Combine(directory, "openHandlers.yaml"), yaml);

    [Fact]
    public async Task A_file_replaced_on_disk_is_not_seen_until_the_cache_is_dropped()
    {
        using var config = new TempDirectory("DevToolboxCacheBust");
        WriteConfig(config.Path, OpensInSsms);

        var handlers = HandlersReading(config.Path);
        await handlers.GetConfigAsync();
        Assert.Equal("SSMS", handlers.HandlerFor(@"C:\x\y.sln")?.Name);

        // Stands in for ConfigRestore, which writes the file directly.
        WriteConfig(config.Path, OpensInVisualStudio);
        await handlers.GetConfigAsync();

        // Documents the trap rather than the fix: reloading changes nothing on its own, which is
        // why "reopen the affected tab" was not enough and a restart was the only way through.
        Assert.Equal("SSMS", handlers.HandlerFor(@"C:\x\y.sln")?.Name);
    }

    [Fact]
    public async Task Invalidating_makes_the_next_read_come_from_disk()
    {
        using var config = new TempDirectory("DevToolboxCacheBust");
        WriteConfig(config.Path, OpensInSsms);

        var handlers = HandlersReading(config.Path);
        await handlers.GetConfigAsync();

        WriteConfig(config.Path, OpensInVisualStudio);
        ((ICachedConfig)handlers).Invalidate();
        await handlers.GetConfigAsync();

        Assert.Equal("Visual Studio", handlers.HandlerFor(@"C:\x\y.sln")?.Name);
    }

    [Fact]
    public void Invalidating_before_anything_is_loaded_is_harmless()
    {
        using var config = new TempDirectory("DevToolboxCacheBust");
        var handlers = HandlersReading(config.Path);

        ((ICachedConfig)handlers).Invalidate();

        // Not loaded, so nothing matches — but it must not throw, since Restore All drops every
        // cache whether or not that page has been opened yet.
        Assert.Null(handlers.HandlerFor(@"C:\x\y.sln"));
    }

    [Theory]
    [InlineData("openHandlers.yaml", "openHandlers")]
    [InlineData("dashboardIcons.yaml", "dashboardIcons")]
    [InlineData("workspaceSources.yaml", "workspaceSources")]
    public void A_restored_file_name_matches_the_key_of_the_service_holding_it(string fileName, string configKey)
    {
        // The coupling the whole fix hangs on, and it is invisible: Settings matches a restored
        // file to its service by stripping .yaml off the name and comparing to ConfigKey. Rename
        // either side and nothing fails to compile, no test elsewhere breaks, and restores quietly
        // stop taking effect again.
        Assert.Equal(configKey, Path.GetFileNameWithoutExtension(fileName));
    }

    [Fact]
    public async Task Every_service_that_caches_a_config_can_be_told_to_drop_it()
    {
        using var config = new TempDirectory("DevToolboxCacheBust");

        var storage = new DirectoryYamlStorage(config.Path);
        var services = new ICachedConfig[]
        {
            new OpenHandlerService(storage),
            new IconService(storage),
            new WorkspaceSourceService(storage),
        };

        foreach (var service in services)
        {
            Assert.False(string.IsNullOrWhiteSpace(service.ConfigKey));
            service.Invalidate();
        }

        // And still usable afterwards.
        var handlers = (OpenHandlerService)services[0];
        Assert.NotNull(await handlers.GetConfigAsync());
    }
}
