using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The parameter form on the Scripts tab is built from the script itself, so this is what decides
/// whether a script can be run from that tab at all.
/// <para>
/// Before it, the tab had one "path to run against" box hard-wired to <c>ProjectPath</c>. A script
/// asking for anything else — an output file, a mode, a switch — had nowhere to be given it, and
/// running it meant editing the parameter's default and saving. The form replaces that, and it is
/// only as good as this parse: a type read wrongly is a text box where a checkbox belongs, and a
/// missed <c>Mandatory</c> is a run that fails with a binding error instead of being stopped.
/// </para>
/// </summary>
public class ScriptParameterTests
{
    private static ScriptParameter One(string script)
    {
        var parameters = PowerShellService.DeclaredParameters(script);
        return Assert.Single(parameters);
    }

    [Fact]
    public void Parameters_keep_the_order_they_are_declared_in()
    {
        const string script = """
            param(
                [string]$ProjectPath,
                [string]$OutputFile,
                [switch]$Force
            )
            """;

        Assert.Equal(
            new[] { "ProjectPath", "OutputFile", "Force" },
            PowerShellService.DeclaredParameters(script).Select(p => p.Name));
    }

    [Theory]
    [InlineData("param([switch]$Force)", ScriptParameterKind.Switch)]
    [InlineData("param([bool]$Force)", ScriptParameterKind.Switch)]
    [InlineData("param([int]$Depth)", ScriptParameterKind.Number)]
    [InlineData("param([double]$Ratio)", ScriptParameterKind.Number)]
    [InlineData("param([string]$Message)", ScriptParameterKind.Text)]
    [InlineData("param($Anything)", ScriptParameterKind.Text)]
    public void The_declared_type_decides_the_control(string script, ScriptParameterKind expected)
    {
        Assert.Equal(expected, One(script).Kind);
    }

    [Theory]
    [InlineData("param([string]$ProjectPath)", ScriptParameterKind.Folder)]
    [InlineData("param([string]$RootDirectory)", ScriptParameterKind.Folder)]
    [InlineData("param([string]$TargetFolder)", ScriptParameterKind.Folder)]
    [InlineData("param([string]$OutputFile)", ScriptParameterKind.File)]
    [InlineData("param([string]$filePath)", ScriptParameterKind.File)]
    public void A_string_that_names_a_path_gets_a_picker(string script, ScriptParameterKind expected)
    {
        Assert.Equal(expected, One(script).Kind);
    }

    [Fact]
    public void A_validate_set_becomes_a_choice_whatever_the_name_suggests()
    {
        // Name says folder, ValidateSet says these three and nothing else. The set wins: it is the
        // only one of the two the script actually enforces.
        var parameter = One("param([ValidateSet('Debug','Release','Both')][string]$OutputPath)");

        Assert.Equal(ScriptParameterKind.Choice, parameter.Kind);
        Assert.Equal(new[] { "Debug", "Release", "Both" }, parameter.AllowedValues);
    }

    [Theory]
    [InlineData("param([Parameter(Mandatory=$true)][string]$ProjectPath)", true)]
    [InlineData("param([Parameter(Mandatory)][string]$ProjectPath)", true)]
    [InlineData("param([Parameter(Mandatory=$false)][string]$ProjectPath)", false)]
    [InlineData("param([string]$ProjectPath)", false)]
    public void Mandatory_is_read_in_both_of_its_spellings(string script, bool expected)
    {
        Assert.Equal(expected, One(script).IsMandatory);
    }

    [Theory]
    [InlineData("param([string]$OutputFile = 'workspaceGroups.yaml')", "workspaceGroups.yaml")]
    [InlineData("param([int]$Depth = 3)", "3")]
    public void A_literal_default_is_offered_as_the_starting_value(string script, string expected)
    {
        Assert.Equal(expected, One(script).DefaultValue);
    }

    [Fact]
    public void An_expression_default_is_left_to_the_script()
    {
        // Prefilling this would send the *text* "(Get-Date)" through as the value. Left empty, the
        // parameter goes unsupplied and PowerShell evaluates the default itself.
        Assert.Equal(string.Empty, One("param([string]$Stamp = (Get-Date).ToString('s'))").DefaultValue);
    }

    [Fact]
    public void A_help_message_is_carried_through_to_the_field()
    {
        var parameter = One("param([Parameter(HelpMessage='Where the solution lives')][string]$ProjectPath)");

        Assert.Equal("Where the solution lives", parameter.HelpMessage);
    }

    [Theory]
    [InlineData("Write-Host 'nothing to ask for'")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_script_with_no_param_block_asks_for_nothing(string script)
    {
        Assert.Empty(PowerShellService.DeclaredParameters(script));
    }

    [Fact]
    public void A_script_that_does_not_parse_asks_for_nothing()
    {
        // Rather than a form that changes shape on every keystroke while a param block is being
        // typed. The editor is live, so half-written scripts are the normal case, not the odd one.
        Assert.Empty(PowerShellService.DeclaredParameters("param([string]$Path"));
    }

    [Fact]
    public void A_param_block_inside_a_function_is_not_the_scripts_own()
    {
        const string script = """
            param([string]$ProjectPath)

            function Find-Things {
                param([string]$dir, [int]$depth)
                $dir
            }
            """;

        Assert.Equal(new[] { "ProjectPath" }, PowerShellService.DeclaredParameters(script).Select(p => p.Name));
    }

    [Theory]
    [InlineData("ProjectPath", "Project Path")]
    [InlineData("OutputFile", "Output File")]
    [InlineData("Force", "Force")]
    [InlineData("filePath", "file Path")]
    [InlineData("IISPath", "IIS Path")]
    public void Labels_are_the_parameter_name_split_into_words(string name, string expected)
    {
        var parameter = One($"param([string]${name})");

        Assert.Equal(expected, parameter.Label);
    }

    [Fact]
    public void Required_parameters_are_the_mandatory_ones_by_name()
    {
        const string script = """
            param(
                [Parameter(Mandatory=$true)][string]$ProjectPath,
                [string]$OutputFile,
                [Parameter(Mandatory)][string]$Target
            )
            """;

        Assert.Equal(new[] { "ProjectPath", "Target" }, PowerShellService.RequiredParameters(script));
    }
}
