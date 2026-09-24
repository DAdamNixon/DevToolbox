using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// What Validate — and Validate on save, which is on by default — will and will not let through.
/// <para>
/// It used to insist on a param() block with a $ProjectPath in it, and refuse to save anything
/// without one. That made the convention the four bundled scripts share (each works on a folder)
/// into a rule for every script anyone writes, including the ones that have no folder to work on.
/// </para>
/// </summary>
public class ScriptValidationTests
{
    private static Services.Models.ScriptValidationResult Validate(string script) =>
        new ScriptValidationService().ValidateScript(script);

    [Fact]
    public void A_script_with_no_param_block_is_valid()
    {
        var result = Validate("Write-Host 'no parameters at all'");

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
    }

    [Fact]
    public void A_script_without_ProjectPath_is_valid()
    {
        var result = Validate("""
            param([string]$OutputFile = 'report.txt')
            Write-Host "Writing $OutputFile"
            """);

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
    }

    [Fact]
    public void A_script_that_does_declare_ProjectPath_is_still_valid()
    {
        var result = Validate("""
            param([Parameter(Mandatory = $true)][string]$ProjectPath)
            Write-Host $ProjectPath
            """);

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
    }

    [Fact]
    public void A_syntax_error_is_still_an_error()
    {
        var result = Validate("if ( { 'never closed'");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.ValidationErrors);
    }

    [Fact]
    public void Nobody_is_told_to_end_a_script_with_ReadKey()
    {
        // Advice from when a terminal run closed its window at the end. It runs with -NoExit now,
        // and in the Scripts tab there is no console at all — ReadKey there throws.
        var result = Validate("Write-Host 'done'");

        Assert.DoesNotContain(result.ValidationWarnings, w => w.Contains("ReadKey"));
    }
}
