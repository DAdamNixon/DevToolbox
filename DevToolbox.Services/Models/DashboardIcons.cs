namespace DevToolbox.Services.Models;

/// <summary>Which kind of dashboard card an icon rule applies to.</summary>
public enum IconScope
{
    Any,
    Group,
    Workspace,
    Location
}

/// <summary>An icon plus an optional accent colour.</summary>
public class IconStyle
{
    public string? Icon { get; set; }
    public string? Color { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Icon) && string.IsNullOrWhiteSpace(Color);
}

/// <summary>Fallback icons used when nothing more specific matches.</summary>
public class IconDefaults
{
    public string Group { get; set; } = "bi-folder2";
    public string Workspace { get; set; } = "bi-code-square";
    public string Location { get; set; } = "bi-folder";
    public string GroupColor { get; set; } = "#3b82f6";
    public string WorkspaceColor { get; set; } = "#8b5cf6";
    public string LocationColor { get; set; } = "#3b82f6";
}

/// <summary>Name-pattern rule: the first rule that matches a card's name wins.</summary>
public class IconRule
{
    /// <summary>Text to look for in the card name, or a regex when <see cref="Regex"/> is true.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Treat <see cref="Match"/> as a regular expression instead of a substring.</summary>
    public bool Regex { get; set; }

    /// <summary>Restrict the rule to one kind of card. Defaults to all of them.</summary>
    public IconScope Scope { get; set; } = IconScope.Any;

    public string? Icon { get; set; }
    public string? Color { get; set; }
}

/// <summary>Exact per-name assignments, written by the icon picker.</summary>
public class IconOverrides
{
    public Dictionary<string, IconStyle> Groups { get; set; } = new();
    public Dictionary<string, IconStyle> Workspaces { get; set; } = new();
    public Dictionary<string, IconStyle> Locations { get; set; } = new();
}

/// <summary>Root of Config/dashboardIcons.yaml.</summary>
public class IconConfig
{
    public IconDefaults Defaults { get; set; } = new();

    /// <summary>Evaluated top to bottom; first match wins.</summary>
    public List<IconRule> Rules { get; set; } = new();

    /// <summary>Beats every rule.</summary>
    public IconOverrides Overrides { get; set; } = new();

    /// <summary>Icons offered by the picker. Any Bootstrap Icons name is still accepted.</summary>
    public List<string> Catalog { get; set; } = new();

    /// <summary>Colours offered by the picker.</summary>
    public List<string> Palette { get; set; } = new();
}
