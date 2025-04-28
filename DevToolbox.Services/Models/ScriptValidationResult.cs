namespace DevToolbox.Services.Models;

/// <summary>
/// Result of script validation
/// </summary>
public class ScriptValidationResult
{
    /// <summary>
    /// Whether the script is valid according to the required structure
    /// </summary>
    public bool IsValid { get; set; }
    
    /// <summary>
    /// Whether the script has non-critical warnings
    /// </summary>
    public bool HasWarnings { get; set; }
    
    /// <summary>
    /// List of validation errors that prevent the script from being valid
    /// </summary>
    public List<string> ValidationErrors { get; set; } = new List<string>();
    
    /// <summary>
    /// List of validation warnings that don't prevent the script from being valid
    /// </summary>
    public List<string> ValidationWarnings { get; set; } = new List<string>();
} 