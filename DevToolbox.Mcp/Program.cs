using DevToolbox.Mcp;
using DevToolbox.Mcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// STDOUT IS THE MCP WIRE from here on. Nothing of ours may ever write to it: every message in this
// project goes to standard error instead, and the transport owns stdout exclusively. Enforced two
// ways, because one is not enough —
//   * StdoutTests      — static source scan for Console.Write/Out/SetOut tokens across
//                        DevToolbox.Mcp. Cannot catch an indirect write; it says so itself. It is
//                        also why this server does not reuse DevToolbox.Services' YamlStorageService,
//                        which has a Console.WriteLine in its catch block.
//   * LoggingSinkTests — runtime: swaps Console.Out, logs at every level, and asserts stdout stayed
//                        empty AND stderr received the records (a dead logger must not pass).
// The configuration that makes the second one true is ServerComposition.ConfigureLogging — read its
// remarks before changing it.

// This process's own scratch database, named for the process. Computed here rather than inside the
// composition so the ownership handle, the sweep and the server all agree on it by construction.
var databasePath = Path.Combine(
    McpLogDatabase.Folder,
    McpLogDatabase.FileNameFor(Environment.ProcessId, DateTime.UtcNow));

// Taken BEFORE the sweep, so this session's database is marked as owned before anything starts
// deciding what looks abandoned. Held for the whole process: releasing it early would make a live
// session's database look abandoned to the next sweep, which would then delete it mid-query.
using var ownership = McpLogDatabase.AcquireOwnership(databasePath);

// Clear out databases left by processes that are gone — a crash or a hard kill skips the disposal
// below. Ownership is proved by the lock file rather than by the database, because the storage layer
// opens and closes a connection per operation and a live session's .db is unlocked most of the time.
McpLogDatabase.Sweep(keep: Path.GetFileName(databasePath));

if (args.Length == 1 && args[0] == "--probe")
{
    // The connectivity canary: proves the config folder is readable and the database is creatable,
    // without standing up a transport. Handled BEFORE any host is built, so --probe can never be
    // affected by transport wiring. Reports on stderr, like everything else here.
    return await Probe.RunAsync(databasePath);
}

if (args.Length > 0)
{
    // Not on stdout even here. An unrecognized argument means no session was ever established, so
    // stdout would be harmless — but the habit is the point, and StdoutTests grants no exceptions.
    await Console.Error.WriteLineAsync("Usage: DevToolbox.Mcp [--probe]");
    await Console.Error.WriteLineAsync("With no arguments, serves MCP over stdio.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

ServerComposition.ConfigureLogging(builder.Logging);
ServerComposition.AddLogViewerTools(builder.Services, databasePath).WithStdioServerTransport();

try
{
    // stdio is a single-session transport: the SDK's own documentation states the server "runs as a
    // single-session service that exits when the stdin stream is closed." Process lifetime is
    // therefore the client's to control, and we add no shutdown logic of our own.
    await builder.Build().RunAsync();
}
finally
{
    // The session is over, so its scratch database is too — the retention policy the Log Viewer
    // already states for itself: results are worth keeping for as long as you are looking at them
    // and not one moment longer.
    //
    // Only ours, by path. Sweeping here instead would be wrong: a sibling session's database is
    // unlocked between its own queries, so a broad sweep at exit could delete a database another
    // agent is still using. A crash skips this entirely, which is what the startup sweep is for.
    ownership.Dispose();
    McpLogDatabase.Delete(databasePath);
}

return 0;
