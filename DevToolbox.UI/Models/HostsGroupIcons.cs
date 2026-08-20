using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.UI.Models;

/// <summary>One offerable icon and the word for it, for the picker.</summary>
public sealed record HostsIconChoice(string Class, string Label);

/// <summary>
/// Which icon a group's card and menu entry carry.
/// <para>
/// A group's name is the obvious thing to pick from, and it is exactly the wrong thing: mapping
/// "DB Server" to a database glyph in code would bake one team's vocabulary into a tool whose whole
/// premise is that it knows nothing about the file it is reading. So the name only ever matters as
/// a key into <see cref="HostsSettings.GroupIcons"/>, which the developer fills in; with no entry
/// there the icon is derived from what the group's entries actually point at, which is generic
/// networking and true of anybody's hosts file.
/// </para>
/// <para>
/// These are Bootstrap Icons classes, not Tailwind utilities, so unlike
/// <see cref="HostsSeverityStyles"/> they can safely be chosen at runtime — the whole icon font's
/// CSS is shipped, rather than generated from what the scanner found in the source.
/// </para>
/// </summary>
public static class HostsGroupIcons
{
    /// <summary>Shown for a group whose entries say nothing useful, and when a configured value is unusable.</summary>
    public const string Fallback = "bi-tag";

    /// <summary>
    /// What the picker offers. Not a limit on what may be configured — any Bootstrap Icons class
    /// works if typed into the YAML — just the ones worth putting in front of somebody.
    /// </summary>
    public static IReadOnlyList<HostsIconChoice> Choices { get; } =
    [
        new("bi-hdd-network", "Network"),
        new("bi-database", "Database"),
        new("bi-server", "Server"),
        new("bi-hdd-rack", "Rack"),
        new("bi-laptop", "This machine"),
        new("bi-pc-display", "Workstation"),
        new("bi-diagram-3", "Cluster"),
        new("bi-router", "Router"),
        new("bi-ethernet", "Wired"),
        new("bi-globe", "Public site"),
        new("bi-globe2", "Region"),
        new("bi-cloud", "Cloud"),
        new("bi-shield-lock", "Secured"),
        new("bi-lightning-charge", "Live"),
        new("bi-box-seam", "Service"),
        new("bi-boxes", "Containers"),
        new("bi-braces", "API"),
        new("bi-terminal", "Tooling"),
        new("bi-cpu", "Compute"),
        new("bi-window-stack", "Front end"),
        new("bi-people", "Team"),
        new("bi-building", "Office"),
        new("bi-printer", "Printing"),
        new("bi-bug", "Testing"),
        new("bi-gear", "Other"),
        new(Fallback, "Plain tag"),
    ];

    /// <summary>
    /// The icon for a group: whatever settings name for it, else one derived from its entries.
    /// </summary>
    /// <param name="overrides">
    /// <see cref="HostsSettings.GroupIcons"/>, or null before settings have loaded.
    /// </param>
    public static string Resolve(HostsGroup group, HostsMap map, IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(map);

        if (overrides is not null &&
            overrides.TryGetValue(group.Name, out var configured) &&
            IsUsable(configured))
        {
            return configured.Trim();
        }

        return Derive(group, map);
    }

    /// <summary>Whether settings name an icon for this group, so the picker can offer "back to automatic".</summary>
    public static bool IsPinned(string groupName, IReadOnlyDictionary<string, string>? overrides) =>
        overrides is not null && overrides.TryGetValue(groupName, out var configured) && IsUsable(configured);

    /// <summary>
    /// A configured value is written straight into a class attribute, so it has to be one class and
    /// an icon one. The <c>bi-</c> prefix and the character restriction together mean a hand-edited
    /// config cannot smuggle in a second class or a layout utility.
    /// </summary>
    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("bi-", StringComparison.Ordinal) || trimmed.Length < 4) return false;

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return false;
        }

        return true;
    }

    /// <summary>
    /// A glyph for the shape the group turned out to have. The decision itself is
    /// <see cref="HostsGroupShapes"/>' — it is about addresses, which is testable and belongs with
    /// the rest of the parsing; only the choice of picture is this layer's.
    /// </summary>
    private static string Derive(HostsGroup group, HostsMap map) => HostsGroupShapes.Of(group, map) switch
    {
        HostsGroupShape.Loopback or HostsGroupShape.LocalOrRemote => "bi-laptop",
        HostsGroupShape.SingleNetwork => "bi-server",
        HostsGroupShape.SeveralNetworks => "bi-diagram-3",
        _ => Fallback,
    };
}
