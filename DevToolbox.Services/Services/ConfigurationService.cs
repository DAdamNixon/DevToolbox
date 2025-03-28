using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DevToolbox.Services.Services;

public class ConfigurationService : IConfigurationService
{
    private readonly IConfiguration _configuration;
    private AppSettings _settings;

    public ConfigurationService(IConfiguration configuration)
    {
        _configuration = configuration;
        _settings = new AppSettings();
        LoadSettings();
    }

    public AppSettings Settings => _settings;

    public T GetValue<T>(string key, T defaultValue)
    {
        return _configuration.GetValue(key, defaultValue) ?? defaultValue;
    }

    public string GetConnectionString(string name)
    {
        return _configuration.GetConnectionString(name) ?? string.Empty;
    }

    public void Reload()
    {
        LoadSettings();
    }

    private void LoadSettings()
    {
        _configuration.Bind(_settings);
    }
} 