namespace DevToolbox.Services.Models;

/// <summary>
/// How a parameter should be asked for. The AST knows the declared .NET type; this is the smaller
/// question of what control to put on screen, which is not the same thing — a <c>[string]</c> with a
/// <c>[ValidateSet]</c> is a dropdown, and a <c>[string]</c> called <c>$ProjectPath</c> deserves a
/// Browse button that a <c>[string]</c> called <c>$Message</c> does not.
/// </summary>
public enum ScriptParameterKind
{
    /// <summary>A line of text.</summary>
    Text,

    /// <summary>Text that names a directory, so the folder picker is worth offering.</summary>
    Folder,

    /// <summary>Text that names a file.</summary>
    File,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A <c>[switch]</c>, or a <c>[bool]</c>. Present or absent, nothing to type.</summary>
    Switch,

    /// <summary>One of a fixed set, from <c>[ValidateSet(...)]</c>.</summary>
    Choice
}

/// <summary>
/// One parameter of a script's <c>param()</c> block, in the terms a form needs: what to label it,
/// what control to draw, whether it can be left blank, and what to put in it to begin with.
/// <para>
/// Parsed from the script itself rather than configured anywhere, so a script that grows a parameter
/// grows a field the next time it is opened, and nothing has to be kept in step by hand.
/// </para>
/// </summary>
/// <param name="Name">The variable name without the sigil — <c>ProjectPath</c>, not <c>$ProjectPath</c>.</param>
/// <param name="Kind">Which control to render.</param>
/// <param name="TypeName">The declared type as written, or empty when the script did not say. Shown
/// as a hint, and used to convert what was typed on the way to PowerShell.</param>
/// <param name="IsMandatory">Declared <c>Mandatory</c>, so the run cannot proceed without it.</param>
/// <param name="DefaultValue">The default as written in the script, when it is a literal worth
/// prefilling; empty otherwise. An expression default (<c>= (Get-Date)</c>) is deliberately not
/// prefilled — the script evaluates it far better than a text box can.</param>
/// <param name="AllowedValues">The <c>[ValidateSet]</c> values, when there are any.</param>
/// <param name="HelpMessage">The <c>HelpMessage</c> from the <c>[Parameter]</c> attribute, when set.</param>
public sealed record ScriptParameter(
    string Name,
    ScriptParameterKind Kind,
    string TypeName,
    bool IsMandatory,
    string DefaultValue,
    IReadOnlyList<string> AllowedValues,
    string HelpMessage)
{
    /// <summary>
    /// A label for the field. PowerShell parameters are PascalCase by convention, which reads as one
    /// long word at label size, so the words are separated: <c>OutputFile</c> becomes "Output File".
    /// </summary>
    public string Label
    {
        get
        {
            var text = new System.Text.StringBuilder(Name.Length + 8);

            for (var i = 0; i < Name.Length; i++)
            {
                var c = Name[i];

                // Break before a capital that starts a new word, but not inside a run of them:
                // "IISPath" is one acronym followed by a word, not seven letters to space out.
                var startsWord = i > 0
                                 && char.IsUpper(c)
                                 && (!char.IsUpper(Name[i - 1]) || (i + 1 < Name.Length && char.IsLower(Name[i + 1])));

                if (startsWord) text.Append(' ');
                text.Append(c);
            }

            return text.ToString();
        }
    }
}
