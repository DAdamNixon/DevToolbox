using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// How a bundled script is handed its path when it runs in a terminal window.
/// <para>
/// This was <c>-File "script.ps1" -ProjectPath 'C:\somewhere'</c>, and <c>-File</c> does not parse
/// what follows it as PowerShell — the apostrophes arrived as part of the value, so the script tried
/// to use a drive called <c>'C</c>. Two of the four bundled scripts happened to strip quotes off
/// their own parameter, which is the only reason they appeared to work.
/// </para>
/// </summary>
public class ScriptArgumentTests
{
    private static string Build(string script, params (string Key, object Value)[] parameters) =>
        SystemService.BuildScriptArguments(script, parameters.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void The_script_runs_through_the_call_operator_not_minus_file()
    {
        var arguments = Build(@"C:\Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\TFS"));

        Assert.Contains(@"-Command ""& 'C:\Scripts\Real-Clean.ps1'", arguments);
        Assert.DoesNotContain("-File", arguments);
    }

    [Fact]
    public void A_value_is_quoted_so_PowerShell_strips_the_quotes_itself()
    {
        var arguments = Build(@"C:\Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\TFS\Tools\DevToolbox"));

        Assert.Contains(@"-ProjectPath 'C:\TFS\Tools\DevToolbox'", arguments);
    }

    [Fact]
    public void A_path_containing_a_space_survives()
    {
        var arguments = Build(@"C:\Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\Program Files\Thing"));

        Assert.Contains(@"-ProjectPath 'C:\Program Files\Thing'", arguments);
    }

    [Fact]
    public void An_apostrophe_in_a_path_is_doubled_rather_than_ending_the_string()
    {
        // Doubling is how a single-quoted PowerShell string escapes a quote. Without it a folder
        // called "Juan's Projects" would end the argument early and run whatever followed.
        var arguments = Build(@"C:\Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\Juan's Projects"));

        Assert.Contains(@"-ProjectPath 'C:\Juan''s Projects'", arguments);
    }

    [Fact]
    public void An_apostrophe_in_the_script_path_is_escaped_too()
    {
        var arguments = Build(@"C:\Juan's Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\TFS"));

        Assert.Contains(@"& 'C:\Juan''s Scripts\Real-Clean.ps1'", arguments);
    }

    [Fact]
    public void Several_parameters_are_all_passed()
    {
        var arguments = Build(@"C:\Scripts\Workspace-Builder.ps1",
            ("ProjectPath", @"C:\TFS"),
            ("OutputFile", "groups.yaml"));

        Assert.Contains(@"-ProjectPath 'C:\TFS'", arguments);
        Assert.Contains("-OutputFile 'groups.yaml'", arguments);
    }

    [Fact]
    public void The_window_is_kept_open_and_policy_is_bypassed()
    {
        var arguments = Build(@"C:\Scripts\Real-Clean.ps1", ("ProjectPath", @"C:\TFS"));

        Assert.StartsWith("-NoExit -ExecutionPolicy Bypass -Command ", arguments);
        Assert.EndsWith("\"", arguments);
    }
}
