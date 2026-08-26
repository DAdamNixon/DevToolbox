using System.Text.RegularExpressions;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Reads Config/dashboardIcons.yaml and answers "what icon does this card get?".
/// Resolution order is override → first matching name rule → source-supplied fallback →
/// configured default, so a user can pin one card without disturbing the rules.
/// </summary>
public class IconService : IIconService
{
    private const string ConfigKey = "dashboardIcons";

    private static readonly string[] DefaultCatalog =
    {
        "bi-folder2", "bi-folder-fill", "bi-code-square", "bi-braces", "bi-terminal",
        "bi-window-stack", "bi-git", "bi-github", "bi-globe2", "bi-globe-americas",
        "bi-cart3", "bi-bag-check", "bi-credit-card", "bi-receipt", "bi-tags",
        "bi-tools", "bi-wrench-adjustable", "bi-hammer", "bi-gear", "bi-sliders",
        "bi-box-seam", "bi-boxes", "bi-archive", "bi-database", "bi-hdd-stack",
        "bi-server", "bi-cloud", "bi-cpu", "bi-lightning-charge", "bi-plug",
        "bi-person-badge", "bi-people", "bi-shield-lock", "bi-key", "bi-door-open",
        "bi-phone", "bi-tablet", "bi-display", "bi-printer", "bi-upc-scan",
        "bi-truck", "bi-building", "bi-shop", "bi-clipboard-data", "bi-graph-up",
        "bi-kanban", "bi-list-check", "bi-journal-code", "bi-book", "bi-bookmarks",
        "bi-envelope", "bi-chat-dots", "bi-bell", "bi-calendar-event", "bi-clock-history",
        "bi-search", "bi-bug", "bi-beaker", "bi-rocket-takeoff", "bi-star"
    };

    private static readonly string[] DefaultPalette =
    {
        "#3b82f6", "#8b5cf6", "#ec4899", "#ef4444", "#f59e0b",
        "#10b981", "#14b8a6", "#06b6d4", "#a1a1aa", "#e4e4e7"
    };

    private readonly IYamlStorageService _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IconConfig? _config;

    public IconService(IYamlStorageService storage)
    {
        _storage = storage;
    }

    public IReadOnlyList<string> Catalog =>
        _config?.Catalog.Count > 0 ? _config.Catalog : DefaultCatalog;

    public IReadOnlyList<string> Palette =>
        _config?.Palette.Count > 0 ? _config.Palette : DefaultPalette;

    public async Task<IconConfig> GetConfigAsync()
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
                    _config = await _storage.LoadAsync<IconConfig>(ConfigKey) ?? new IconConfig();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"IconService: could not read {ConfigKey}.yaml — {ex.Message}");
                    _config = new IconConfig();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _config;
    }

    public async Task SaveConfigAsync(IconConfig config)
    {
        await _storage.SaveAsync(ConfigKey, config);
        _config = config;
    }

    string ICachedConfig.ConfigKey => ConfigKey;

    /// <summary>Drops the loaded icon rules so the next read comes from disk.</summary>
    public void Invalidate() => _config = null;

    public IconStyle Resolve(IconScope scope, string name, IconStyle? fallback = null)
    {
        var config = _config ?? new IconConfig();
        name ??= string.Empty;

        var resolved = new IconStyle();

        if (TryGetOverride(config, scope, name, out var pinned))
        {
            resolved.Icon = pinned.Icon;
            resolved.Color = pinned.Color;
        }

        if (resolved.Icon is null || resolved.Color is null)
        {
            var rule = config.Rules.FirstOrDefault(r => Matches(r, scope, name));
            resolved.Icon ??= NullIfBlank(rule?.Icon);
            resolved.Color ??= NullIfBlank(rule?.Color);
        }

        resolved.Icon ??= NullIfBlank(fallback?.Icon);
        resolved.Color ??= NullIfBlank(fallback?.Color);

        resolved.Icon ??= scope switch
        {
            IconScope.Group => config.Defaults.Group,
            IconScope.Location => config.Defaults.Location,
            _ => config.Defaults.Workspace
        };

        resolved.Color ??= scope switch
        {
            IconScope.Group => config.Defaults.GroupColor,
            IconScope.Location => config.Defaults.LocationColor,
            _ => config.Defaults.WorkspaceColor
        };

        return resolved;
    }

    public async Task SetOverrideAsync(IconScope scope, string name, IconStyle? style)
    {
        var config = await GetConfigAsync();
        var bucket = BucketFor(config, scope);

        // Overrides are keyed by display name, which the user may re-case at will.
        var existingKey = bucket.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existingKey is not null)
        {
            bucket.Remove(existingKey);
        }

        if (style is not null && !style.IsEmpty)
        {
            bucket[name] = style;
        }

        await SaveConfigAsync(config);
    }

    private static bool TryGetOverride(IconConfig config, IconScope scope, string name, out IconStyle style)
    {
        var bucket = BucketFor(config, scope);
        var key = bucket.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (key is not null)
        {
            style = bucket[key];
            return true;
        }

        style = new IconStyle();
        return false;
    }

    private static Dictionary<string, IconStyle> BucketFor(IconConfig config, IconScope scope) => scope switch
    {
        IconScope.Group => config.Overrides.Groups,
        IconScope.Location => config.Overrides.Locations,
        _ => config.Overrides.Workspaces
    };

    private static bool Matches(IconRule rule, IconScope scope, string name)
    {
        if (rule.Scope != IconScope.Any && rule.Scope != scope)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rule.Match))
        {
            return false;
        }

        if (!rule.Regex)
        {
            return name.Contains(rule.Match, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Regex.IsMatch(name, rule.Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"IconService: rule '{rule.Match}' is not a valid regex — {ex.Message}");
            return false;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
