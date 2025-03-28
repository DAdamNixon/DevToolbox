namespace DevToolbox.Services.Models;

public class AppSettings
{
    public DatabaseSettings Database { get; set; } = new();
    public ApiSettings Api { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public class DatabaseSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public int MaxRetries { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class ApiSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public string ApiKey { get; set; } = string.Empty;
}

public class LoggingSettings
{
    public string LogLevel { get; set; } = "Information";
    public string LogFilePath { get; set; } = "logs/app.log";
    public bool EnableConsoleLogging { get; set; } = true;
} 