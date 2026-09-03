using System.Reflection;
using System.Text.RegularExpressions;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// STDOUT IS THE MCP WIRE. A single stray byte written to it corrupts the JSON-RPC session, and
/// the symptom — a client that mysteriously fails to enumerate tools, or a call that never returns
/// — looks nothing like the cause, which is why this is a test rather than a code-review habit.
/// <para>
/// This is the STATIC half: a source scan for the tokens that write to stdout. <b>It cannot catch
/// an indirect write</b> — a logging provider configured to stdout, or a library writing on our
/// behalf — and it does not pretend to. <c>LoggingSinkTests</c> is the runtime half that covers
/// what this cannot.
/// </para>
/// <para>
/// It also documents a real finding: <c>DevToolbox.Services</c>' own <c>YamlStorageService</c> has
/// a <c>Console.WriteLine</c> in the catch block of <c>LoadAsync</c>. Harmless in the UI, fatal
/// here — and the reason the MCP server has its own storage implementation rather than reusing it.
/// That is asserted below, so the day someone "simplifies" the server onto the shared class, this
/// test explains why not.
/// </para>
/// </summary>
public sealed class StdoutTests
{
    /// <summary>
    /// Console.Error and Console.OpenStandardError are fine — stderr is where everything here goes.
    /// Anything else on Console that produces output, or redirects Out, is not.
    /// </summary>
    private static readonly Regex StdoutWrite = new(
        @"Console\s*\.\s*(Write|WriteLine|Out|SetOut|OpenStandardOutput)\b",
        RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DevToolbox.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void No_source_file_in_the_mcp_server_writes_to_stdout()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFilesOf("DevToolbox.Mcp"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Comments discuss stdout constantly — that is the point of them. Only code counts.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                    continue;

                if (StdoutWrite.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These write to stdout, which is the JSON-RPC wire:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_mcp_server_writes_to_stderr_somewhere_so_the_scan_above_is_not_vacuous()
    {
        // Without this, deleting every Console call in the project would make the test above pass
        // for the wrong reason. Program.cs and Probe.cs both report on stderr.
        var writesToStderr = SourceFilesOf("DevToolbox.Mcp")
            .Any(f => File.ReadAllText(f).Contains("Console.Error.WriteLine", StringComparison.Ordinal));

        Assert.True(writesToStderr, "expected the server to report on stderr");
    }

    [Fact]
    public void The_shared_YamlStorageService_still_writes_to_stdout_which_is_why_the_server_does_not_use_it()
    {
        // Not a complaint about that class — in the UI a diagnostic on the console is fine. This
        // pins the REASON the MCP server has McpYamlStorage instead, so the day someone folds the
        // two together, a test explains the cost rather than a session discovering it on the wire.
        //
        // If this ever goes red because the Console.WriteLine was removed, that is good news: delete
        // this test and reconsider whether McpYamlStorage still needs to exist. Its other two
        // reasons (no config seeding, no delete path) stand on their own.
        var shared = Path.Combine(RepoRoot(), "DevToolbox.Services", "Services", "YamlStorageService.cs");
        Assert.True(File.Exists(shared), shared);

        Assert.Contains("Console.WriteLine", File.ReadAllText(shared), StringComparison.Ordinal);
    }
}
