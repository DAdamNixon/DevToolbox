using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

public class ScriptValidationService
{
    /// <summary>
    /// Validates a PowerShell script: an error for anything that stops it parsing, and warnings
    /// for style.
    /// <para>
    /// It also used to require a param() block with a $ProjectPath in it, and with Validate on
    /// save — the default — refused to save any script without one. That was the four bundled
    /// scripts' convention (each works on a folder) made into a rule for every script, including
    /// the ones with no folder to work on. What a script takes is the script's business; the
    /// Scripts tab builds its form from whatever the param() block declares, or from none.
    /// </para>
    /// </summary>
    /// <param name="scriptContent">The content of the script to validate</param>
    /// <returns>Validation result with details of any issues found</returns>
    public ScriptValidationResult ValidateScript(string scriptContent)
    {
        var result = new ScriptValidationResult
        {
            IsValid = true
        };

        try
        {
            // Parse the PowerShell script
            var scriptAst = Parser.ParseInput(scriptContent, out Token[] tokens, out ParseError[] errors);
            
            // Check for parsing errors
            if (errors != null && errors.Length > 0)
            {
                result.IsValid = false;
                result.ValidationErrors.Add("Script contains syntax errors.");
                foreach (var error in errors)
                {
                    result.ValidationErrors.Add($"Line {error.Extent.StartLineNumber}: {error.Message}");
                }
                return result;
            }

            // No "wait logic" warning any more. It told every script to end with ReadKey so a
            // terminal run's window stayed open; terminal runs are started with -NoExit now, and in
            // the Scripts tab there is no console at all, so ReadKey there is an error, not a pause.

            // Check for proper comments/documentation
            bool hasComments = HasProperComments(scriptContent);
            if (!hasComments)
            {
                result.HasWarnings = true;
                result.ValidationWarnings.Add("Script should include descriptive comments at the beginning.");
            }
            
            // Warning for too many Mandatory parameters 
            int mandatoryCount = CountMandatoryParameters(scriptContent);
            if (mandatoryCount > 2)
            {
                result.HasWarnings = true;
                result.ValidationWarnings.Add($"Script has {mandatoryCount} mandatory parameters. Consider making some optional.");
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.ValidationErrors.Add($"Error validating script: {ex.Message}");
        }
        
        return result;
    }
    
    private bool HasProperComments(string scriptContent)
    {
        // Check for comments at the beginning
        var match = Regex.Match(scriptContent, @"^\s*#.*[\r\n](\s*#.*[\r\n])+", RegexOptions.Multiline);
        return match.Success;
    }
    
    private int CountMandatoryParameters(string scriptContent)
    {
        return Regex.Matches(scriptContent, @"\[Parameter\s*\(.*Mandatory\s*=\s*\$true.*\)\]", 
            RegexOptions.IgnoreCase | RegexOptions.Singleline).Count;
    }
} 