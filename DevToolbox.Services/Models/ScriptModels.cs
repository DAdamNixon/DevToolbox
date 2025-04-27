namespace DevToolbox.Services.Models
{
    /// <summary>
    /// Represents information about a PowerShell script
    /// </summary>
    public class ScriptInfo
    {
        /// <summary>
        /// The name of the script (without extension)
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// The full path to the script file
        /// </summary>
        public string FullPath { get; set; } = string.Empty;
        
        /// <summary>
        /// The last modified date of the script file
        /// </summary>
        public DateTime LastModified { get; set; }
    }
} 