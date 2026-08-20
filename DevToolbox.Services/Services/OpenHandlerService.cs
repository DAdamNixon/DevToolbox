using System.Text.RegularExpressions;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Answers "which program opens this file?" from Config/openHandlers.yaml.
/// </summary>
public class OpenHandlerService : IOpenHandlerService
{
    private const string ConfigKey = "openHandlers";

    private readonly IYamlStorageService _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenHandlerConfig? _config;

    public OpenHandlerService(IYamlStorageService storage)
    {
        _storage = storage;
    }

    public async Task<OpenHandlerConfig> GetConfigAsync()
    {
        if (_config is not null)
        {
            return _config;
        }

        await _gate.WaitAsync();
        try
        {
            if (_config is null)
            {
                try
                {
                    _config = await _storage.LoadAsync<OpenHandlerConfig>(ConfigKey) ?? new OpenHandlerConfig();
                }
                catch (Exception ex)
                {
                    // A broken handler file should cost us the handlers, not the dashboard.
                    Console.WriteLine($"OpenHandlerService: could not read {ConfigKey}.yaml — {ex.Message}");
                    _config = new OpenHandlerConfig();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _config;
    }

    public async Task SaveConfigAsync(OpenHandlerConfig config)
    {
        await _storage.SaveAsync(ConfigKey, config);
        _config = config;
    }

    public CustomOpenOption? HandlerFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _config is null)
        {
            return null;
        }

        return _config.Handlers.FirstOrDefault(h => h.IsUsable && Matches(h.Match, path));
    }

    /// <summary>
    /// Glob match. A pattern with a path separator is tested against the whole path so a
    /// rule can be scoped to one tree; otherwise just the file name is tested.
    /// </summary>
    private static bool Matches(string pattern, string path)
    {
        var scoped = pattern.Contains('\\') || pattern.Contains('/');
        var subject = scoped ? path : Path.GetFileName(path);

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        try
        {
            return Regex.IsMatch(subject, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
