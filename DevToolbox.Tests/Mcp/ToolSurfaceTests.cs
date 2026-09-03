using System.Reflection;
using DevToolbox.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// Guardrail #1 asserted at the protocol layer, which is where a caller actually meets it.
/// Everything else proves the tools behave; these prove the SURFACE is what the server claims —
/// ten tools, no more, with exactly one of them writing anything persistent.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] ExpectedToolNames =
    {
        "list_locations",
        "list_templates",
        "get_template",
        "list_log_files",
        "prepare_table",
        "query_entries",
        "describe_columns",
        "split_groups",
        "list_saved_queries",
        "save_query",
    };

    private static IReadOnlyList<McpServerTool> RegisteredTools()
    {
        // Composed exactly as Program.cs composes it, minus the transport — a transport would want a
        // real stdin/stdout. If this drifts from Program.cs the tests stop testing the shipped
        // surface, which is why ServerComposition exists as one shared entry point.
        var services = new ServiceCollection();
        services.AddLogging();
        ServerComposition.AddLogViewerTools(services, Path.Combine(Path.GetTempPath(), "devtoolbox-mcp-surface.db"));

        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().ToList();
    }

    [Fact]
    public void Exactly_ten_tools_are_registered()
    {
        // Red against an eleventh tool, or against WithToolsFromAssembly picking up a stray
        // [McpServerToolType] — which is exactly why registration is an explicit list.
        Assert.Equal(10, RegisteredTools().Count);
    }

    [Fact]
    public void The_tool_names_are_exactly_the_ten_wire_names()
    {
        // Set EQUALITY, not Contains: an eleventh tool named delete_query would pass a Contains
        // check on all ten of these.
        var actual = RegisteredTools().Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expected = ExpectedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Only_prepare_table_and_save_query_are_declared_as_writing()
    {
        // The ReadOnly hint is what a client shows a user before approving a call, so it has to be
        // true rather than aspirational. prepare_table writes to this session's scratch database;
        // save_query appends to the developer's saved_queries.yaml. Nothing else writes at all.
        var writing = RegisteredTools()
            .Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint != true)
            .Select(t => t.ProtocolTool.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "prepare_table", "save_query" }, writing);
    }

    [Fact]
    public void There_is_no_tool_that_deletes_or_renames_anything()
    {
        // ISavedQueryService offers DeleteAsync and RenameGroupAsync, and ILogConfigService offers
        // template and location writes. None of them is reachable from here, and the point of this
        // test is that adding one has to be a deliberate act that turns a test red.
        var names = RegisteredTools().Select(t => t.ProtocolTool.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("delete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("rename", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("remove", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("save_template", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_tool_carries_a_description()
    {
        // The description is the entire user manual an agent gets. A tool without one is a tool
        // that will be called wrongly.
        foreach (var tool in RegisteredTools())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
                $"{tool.ProtocolTool.Name} has no description");
        }
    }

    [Fact]
    public void The_tools_that_return_log_content_warn_that_it_is_untrusted()
    {
        // Log rows carry text typed by website users. An agent has no other way to learn that, and
        // the description is the only place it can be told before it reads the first row.
        var byName = RegisteredTools().ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.Description ?? "");

        Assert.Contains("instructions", byName["query_entries"], StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The structural guardrail: this process must not be able to load the rest of the toolbox.
/// <para>
/// DevToolbox.Services carries Microsoft.PowerShell.SDK — in-process script execution — and the
/// hosts-file editor with its elevated-command path. The DB2 server's own rule is that the
/// outermost defence is the absence of a code path, and it excluded payroll by not referencing the
/// assembly. This is the same move, and this test is what keeps it true: a well-meaning
/// <c>dotnet add reference</c> turns it red instead of quietly re-linking everything the split
/// exists to exclude.
/// </para>
/// </summary>
public sealed class McpDependencyTests
{
    private static readonly string[] Forbidden =
    {
        "DevToolbox.Services",
        "Microsoft.PowerShell",
        "System.Management.Automation",
    };

    [Fact]
    public void The_mcp_assembly_does_not_reference_the_rest_of_the_toolbox()
    {
        var mcp = typeof(ServerComposition).Assembly;
        var referenced = mcp.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        foreach (var name in Forbidden)
        {
            Assert.DoesNotContain(referenced, r => r.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_logs_library_does_not_reference_the_rest_of_the_toolbox_either()
    {
        // The reference DevToolbox.Mcp *does* have. If PowerShell came back in through here, the
        // test above would still pass and the guardrail would be gone.
        var logs = typeof(DevToolbox.Services.Services.DbLogService).Assembly;

        Assert.Equal("DevToolbox.Logs", logs.GetName().Name);

        var referenced = logs.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        foreach (var name in Forbidden)
        {
            Assert.DoesNotContain(referenced, r => r.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>
/// The runtime half of the stdout guarantee, covering what the source scan cannot: a logging
/// provider whose default sink is stdout.
/// </summary>
public sealed class LoggingSinkTests
{
    [Fact]
    public void Logging_goes_to_stderr_and_never_to_stdout()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            using var factory = LoggerFactory.Create(ServerComposition.ConfigureLogging);
            var log = factory.CreateLogger("DevToolbox.Mcp.Test");

            log.LogTrace("trace");
            log.LogDebug("debug");
            log.LogInformation("information");
            log.LogWarning("warning");
            log.LogError("error");
            log.LogCritical("critical");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        // Both halves matter. Stdout empty alone would pass with logging switched off entirely —
        // a dead logger must not pass — so stderr has to have received the records too.
        Assert.True(string.IsNullOrEmpty(stdout.ToString()),
            $"logging reached stdout, which is the JSON-RPC wire: {stdout}");

        var errorText = stderr.ToString();
        Assert.Contains("information", errorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("critical", errorText, StringComparison.OrdinalIgnoreCase);
    }
}
