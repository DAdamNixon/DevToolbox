using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The Scripts tab's Run button, which never worked.
/// <para>
/// Two independent faults made it look dead. Parameters were "passed" with a second
/// <c>AddScript</c>, which builds a pipeline rather than a preamble, so a script declaring
/// <c>[Parameter(Mandatory=$true)]$ProjectPath</c> — which every bundled script does — got nothing
/// bound and PowerShell tried to prompt, with no console to prompt on. And only the pipeline and
/// error streams were read, while every one of those scripts reports what it is doing with
/// <c>Write-Host</c>, which goes to the information stream. A script could do its whole job and show
/// an empty pane.
/// </para>
/// </summary>
public class PowerShellExecutionTests
{
    private const string TakesAMandatoryPath = """
        param(
            [Parameter(Mandatory=$true)]
            [string]$ProjectPath
        )
        Write-Host "looking at $ProjectPath"
        """;

    private static PowerShellService Service() => new();

    [Fact]
    public async Task A_declared_parameter_is_bound_to_the_script()
    {
        var (output, error) = await Service().ExecuteScriptWithParametersAsync(
            TakesAMandatoryPath,
            new Dictionary<string, object> { ["ProjectPath"] = @"C:\TFS\Tools\DevToolbox" });

        Assert.Empty(error);
        Assert.Contains(@"looking at C:\TFS\Tools\DevToolbox", output);
    }

    [Fact]
    public async Task Write_Host_output_is_captured()
    {
        // It goes to the information stream, not the pipeline. Nothing read it, so every bundled
        // script appeared to produce nothing whatsoever.
        var (output, _) = await Service().ExecuteScriptWithParametersAsync("Write-Host 'hello from the script'");

        Assert.Contains("hello from the script", output);
    }

    [Fact]
    public async Task A_missing_mandatory_parameter_is_explained_rather_than_hanging()
    {
        var (output, error) = await Service().ExecuteScriptWithParametersAsync(TakesAMandatoryPath);

        Assert.Empty(output);
        Assert.Contains("ProjectPath", error);
        Assert.Contains("path box", error);
    }

    [Fact]
    public async Task A_syntax_error_is_reported_instead_of_running()
    {
        var (output, error) = await Service().ExecuteScriptWithParametersAsync("if ( { 'never closed'");

        Assert.Empty(output);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task A_parameter_the_script_does_not_declare_is_available_as_a_variable()
    {
        // Binding it would be an error, but a script may still read $ProjectPath directly, which is
        // what the old code supported and what this keeps working.
        var (output, error) = await Service().ExecuteScriptWithParametersAsync(
            "Write-Host \"got $ProjectPath\"",
            new Dictionary<string, object> { ["ProjectPath"] = "C:\\somewhere" });

        Assert.Empty(error);
        Assert.Contains(@"got C:\somewhere", output);
    }

    [Fact]
    public async Task Errors_raised_by_the_script_come_back_as_errors()
    {
        var (_, error) = await Service().ExecuteScriptWithParametersAsync("Write-Error 'that did not work'");

        Assert.Contains("that did not work", error);
    }

    [Fact]
    public async Task A_terminating_error_is_reported_rather_than_thrown()
    {
        // It never reaches the error stream, so before this the run ended with no output and no
        // explanation at all.
        var (_, error) = await Service().ExecuteScriptWithParametersAsync("throw 'stopped'");

        Assert.Contains("stopped", error);
    }

    [Fact]
    public async Task Warnings_are_shown_with_the_output()
    {
        var (output, _) = await Service().ExecuteScriptWithParametersAsync("Write-Warning 'be careful'");

        Assert.Contains("be careful", output);
    }

    [Theory]
    [InlineData("param([Parameter(Mandatory=$true)][string]$ProjectPath)", "ProjectPath")]
    [InlineData("param([Parameter(Mandatory)][string]$Path)", "Path")]
    public void Required_parameters_are_discoverable_before_running(string script, string expected)
    {
        Assert.Equal(new[] { expected }, PowerShellService.RequiredParameters(script));
    }

    [Theory]
    [InlineData("param([string]$Optional)")]
    [InlineData("param([Parameter(Mandatory=$false)][string]$Optional)")]
    [InlineData("Write-Host 'no parameters at all'")]
    [InlineData("")]
    [InlineData("if ( { 'syntax error'")]
    public void Nothing_is_required_when_nothing_is_mandatory(string script)
    {
        Assert.Empty(PowerShellService.RequiredParameters(script));
    }
}
