using DevToolbox.Services.Services;

namespace DevToolbox.Services.Models;

/// <summary>
/// Represents the result of a PowerShell script save operation with validation details
/// </summary>
public class ScriptSaveResult
{
    /// <summary>
    /// Whether the script was successfully saved
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Path to the saved script if successful
    /// </summary>
    public string ScriptPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Error message if the save operation failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Warning message for successful saves with issues
    /// </summary>
    public string WarningMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Validation result if validation was performed
    /// </summary>
    public ScriptValidationResult ValidationResult { get; set; } = null;
} 