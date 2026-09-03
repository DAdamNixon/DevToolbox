using DevToolbox.Mcp.Core;
using DevToolbox.Services.Services;

namespace DevToolbox.Mcp;

/// <summary>
/// <c>--probe</c>: the connectivity canary, kept for the same reason the DB2 server keeps one.
/// <para>
/// When an MCP server does not work, the client reports almost nothing — a tool list that fails to
/// populate, and no way to tell "the binary is broken" from "the config is missing" from "the
/// client's command line is wrong". A probe that runs the same reads the tools do, prints what it
/// found on stderr, and returns an exit code separates those three in one command.
/// </para>
/// <para>
/// It writes to <b>stderr</b> and never stdout, even though no session exists yet and stdout would
/// be harmless here. The habit is the point, and <c>StdoutTests</c> grants no exceptions.
/// </para>
/// </summary>
internal static class Probe
{
    internal static async Task<int> RunAsync(string databasePath)
    {
        try
        {
            var yaml = new McpYamlStorage();
            await Console.Error.WriteLineAsync($"config      : {yaml.StorageDirectory}");

            if (!Directory.Exists(yaml.StorageDirectory))
            {
                await Console.Error.WriteLineAsync("FAIL        : config folder does not exist. Run DevToolbox once to create it.");
                return 2;
            }

            var storage = new SqliteLogStorageService(databasePath);
            var reader = new DbLogService(yaml, storage);

            var templates = await reader.GetAvailableLogFileTemplatesAsync();
            var locations = await reader.GetLogLocationsAsync();

            var usable = locations.Where(LocationPolicy.IsUsable).ToList();
            var refused = locations.Count - usable.Count;

            await Console.Error.WriteLineAsync($"templates   : {templates.Count} ({string.Join(", ", templates.Select(t => t.Name))})");
            await Console.Error.WriteLineAsync($"locations   : {usable.Count} usable, {refused} refused as misconfigured");

            foreach (var location in usable)
            {
                // Reachability, not validity. A UNC share is unreachable for reasons that have nothing
                // to do with log_paths.yaml — server down, VPN off, no rights today — so the probe
                // reports what it saw rather than calling a correct config broken.
                var exists = Directory.Exists(location.Path) ? "present" : "not reachable now";
                await Console.Error.WriteLineAsync($"  - {location.Name}: {location.Path} [{exists}]");
            }

            // Proves the database is creatable and writable, which is the other half of "can this
            // server actually do its job". Removed again immediately: a probe must not leave state.
            await storage.EnsureTableAsync("probe", new[] { "ok" });
            await storage.DropTableAsync("probe");
            await Console.Error.WriteLineAsync($"database    : {Path.GetFileName(databasePath)} created and writable");

            if (usable.Count == 0)
            {
                await Console.Error.WriteLineAsync("FAIL        : no usable locations. Every configured location has a blank or non-qualified path.");
                return 3;
            }

            await Console.Error.WriteLineAsync("OK");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"FAIL        : {SafeError.Describe(ex)}");
            return 1;
        }
        finally
        {
            McpLogDatabase.Delete(databasePath);
        }
    }
}
