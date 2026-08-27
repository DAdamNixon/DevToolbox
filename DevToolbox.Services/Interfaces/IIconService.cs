using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Resolves the icon and accent colour for a dashboard card from
/// Config/dashboardIcons.yaml: explicit override, then name rule, then default.
/// </summary>
public interface IIconService : ICachedConfig
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

    /// <summary>
    /// Moves an explicit override onto a new name, for when a card is renamed. Does nothing when
    /// the old name has no override, so a card showing a rule-derived icon keeps deriving it rather
    /// than having that icon frozen in place by the rename.
    /// </summary>
    Task RenameOverrideAsync(IconScope scope, string oldName, string newName);

    /// <summary>Icons offered by the picker.</summary>
    IReadOnlyList<string> Catalog { get; }

    /// <summary>Colours offered by the picker.</summary>
    IReadOnlyList<string> Palette { get; }
}
