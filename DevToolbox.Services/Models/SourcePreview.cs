namespace DevToolbox.Services.Models;

/// <summary>
/// What one <see cref="WorkspaceSource"/> would put on the dashboard, worked out without
/// saving anything. The Scan Folders dialog renders this while you type, so a pattern or a
/// regex can be got right before it is committed rather than by saving and squinting at the
/// cards afterwards.
/// <para>
/// Built by the same code that builds the real thing — see
/// <c>WorkspaceSourceService.BuildGroup</c> — so a preview that looks right is not a
/// separate implementation that happens to agree.
/// </para>
/// </summary>
public class SourcePreview
{
    /// <summary>The scanned folder with environment variables expanded.</summary>
    public string ResolvedPath { get; set; } = string.Empty;

    public bool PathExists { get; set; }

    /// <summary>Dashboard group these results would land in.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Why <see cref="WorkspaceSource.NameRegex"/> was ignored, when it was.</summary>
    public string? RegexError { get; set; }

    /// <summary>Set when enumerating the folder threw — a bad pattern, or no permission.</summary>
    public string? Error { get; set; }

    /// <summary>Entries the pattern matched on disk, before the cap.</summary>
    public int EntriesFound { get; set; }

    /// <summary>
    /// True when <see cref="EntriesFound"/> hit the preview cap. The counts below then
    /// describe the sample rather than the whole folder, which the dialog says out loud.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>The cards this source would produce, in the order they would appear.</summary>
    public List<PreviewWorkspace> Workspaces { get; set; } = new();

    /// <summary>
    /// Entries the regex did not match, which fall back to one card each under their whole
    /// name. Usually the interesting half of a preview: it is where a regex that is nearly
    /// right shows itself.
    /// </summary>
    public List<string> Unmatched { get; set; } = new();

    public int WorkspaceCount => Workspaces.Count;

    public int LocationCount => Workspaces.Sum(w => w.Locations.Count);
}

/// <summary>One card in a preview.</summary>
public class PreviewWorkspace
{
    public string Name { get; set; } = string.Empty;

    public List<PreviewLocation> Locations { get; set; } = new();
}

/// <summary>One row inside a previewed card.</summary>
public class PreviewLocation
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>The entry name the workspace/location split was derived from.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>Subtitle from <see cref="WorkspaceSource.DescriptionFrom"/>, if any hit.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// False when the name regex did not match this entry and it fell back to its whole name.
    /// Flagged rather than hidden: a preview full of fallbacks is the signal that the regex is
    /// wrong, and hiding it would make the fallback look like the intended result.
    /// </summary>
    public bool RegexMatched { get; set; } = true;
}
