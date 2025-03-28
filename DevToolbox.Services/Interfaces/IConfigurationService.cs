using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

public interface IConfigurationService
{
    AppSettings Settings { get; }
    T GetValue<T>(string key, T defaultValue);
    string GetConnectionString(string name);
    void Reload();
} 