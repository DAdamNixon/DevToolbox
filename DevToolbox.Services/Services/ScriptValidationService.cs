using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

public class ScriptValidationService
{
    // Required parameter names that must be declared in scripts
    private readonly string[] _requiredParameters = new[] 
    { 
        "ProjectPath"
    };

    // Optional parameter names
    private readonly string[] _optionalParameters = new[]
    {
        "LocationPath",
        "filePath",
        "RootDirectory"
    };

    /// <summary>
    /// Validates a PowerShell script's structure to ensure it meets DevToolbox standards
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
            
            // Check for param block
            var paramBlock = scriptAst.FindAll(ast => ast is ParamBlockAst, true)
                .OfType<ParamBlockAst>()
                .FirstOrDefault();
            
            if (paramBlock == null)
            {
                result.IsValid = false;
                result.ValidationErrors.Add("Script must have a param() block at the beginning.");
                return result;
            }
            
            // Check for required parameters
            var parameters = paramBlock.Parameters;
            var declaredParams = parameters.Select(p => p.Name.VariablePath.UserPath).ToList();
            
            foreach (var requiredParam in _requiredParameters)
            {
                if (!declaredParams.Contains(requiredParam, StringComparer.OrdinalIgnoreCase))
                {
                    result.IsValid = false;
                    result.ValidationErrors.Add($"Script is missing required parameter: {requiredParam}");
                }
            }
            
            // Check for wait logic at the end (ReadKey or similar)
            bool hasWaitLogic = HasWaitLogic(scriptContent);
            if (!hasWaitLogic)
            {
                result.HasWarnings = true;
                result.ValidationWarnings.Add("Script should include wait logic at the end (e.g., $Host.UI.RawUI.ReadKey()) to keep the window open for user interaction.");
            }
            
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
    
    private bool HasWaitLogic(string scriptContent)
    {
        // Check for common wait patterns
        return scriptContent.Contains("$Host.UI.RawUI.ReadKey") || 
               scriptContent.Contains("Read-Host") ||
               scriptContent.Contains("pause") ||
               scriptContent.Contains("Wait-Event");
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