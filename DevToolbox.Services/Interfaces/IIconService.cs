using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Resolves the icon and accent colour for a dashboard card from
/// Config/dashboardIcons.yaml: explicit override, then name rule, then default.
/// </summary>
public interface IIconService
{
    Task<IconConfig> GetConfigAsync();

    Task SaveConfigAsync(IconConfig config);

    /// <summary>
    /// Icon and colour for a card. <paramref name="fallback"/> lets a caller supply its
    /// own default (a workspace source's icon, say) ahead of the configured default.
    /// </summary>
    IconStyle Resolve(IconScope scope, string name, IconStyle? fallback = null);

    /// <summary>Pins an icon to one card name. Pass null to drop the override.</summary>
    Task SetOverrideAsync(IconScope scope, string name, IconStyle? style);

    /// <summary>Icons offered by the picker.</summary>
    IReadOnlyList<string> Catalog { get; }

    /// <summary>Colours offered by the picker.</summary>
    IReadOnlyList<string> Palette { get; }
}
