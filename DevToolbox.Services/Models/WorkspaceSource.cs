using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models;

/// <summary>
/// Root of Config/workspaceSources.yaml. Every folder DevToolbox scans for
/// projects is declared here — nothing about a machine's layout is baked into the app.
/// </summary>
public class WorkspaceSourceConfig
{
    public List<WorkspaceSource> Sources { get; set; } = new();
}

/// <summary>What a source picks up off disk.</summary>
public enum ScanKind
{
    Files,
    Directories
}

/// <summary>What <see cref="WorkspaceSource.NameRegex"/> is matched against.</summary>
public enum NameMatch
{
    /// <summary>The entry's own name, extension dropped. One folder of siblings.</summary>
    Name,

    /// <summary>
    /// The entry's path below the scanned folder, extension kept. For layouts that put the
    /// thing that distinguishes two copies in a parent directory rather than in the file
    /// name — <c>&lt;repo&gt;\development\…</c> against <c>&lt;repo&gt;\demo\…</c>, where every
    /// leaf is called <c>Checkout.sln</c> and only the path says which branch it is.
    /// </summary>
    RelativePath
}

/// <summary>
/// A folder that is scanned for project entry points (.code-workspace, .sln, repo
/// folders, …). Discovered items show up on the dashboard as a read-only group that
/// is rebuilt on every scan, so adding a file to the folder adds a card.
/// </summary>
public class WorkspaceSource
{
    /// <summary>Identifies the source in config and in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Folder to scan. Environment variables such as %USERPROFILE% are expanded.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Glob applied to the entries in <see cref="Path"/>, e.g. <c>*.code-workspace</c>.</summary>
    public string Pattern { get; set; } = "*";

    /// <summary>Whether files or subdirectories are the thing being collected.</summary>
    public ScanKind Scan { get; set; } = ScanKind.Files;

    public bool Recursive { get; set; }

    /// <summary>
    /// Directory and entry names the scan skips, as the same globs <see cref="Pattern"/> uses —
    /// <c>bin</c>, <c>.vs</c>, <c>node_modules</c>. Matched against each path segment, so an
    /// excluded directory is never descended into rather than filtered out afterwards. On a
    /// website working copy that is the difference between walking 3,900 directories and 78,000,
    /// which is what makes <see cref="Recursive"/> usable over a whole repository.
    /// </summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Dashboard group the results land in. Falls back to <see cref="Name"/>.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Bootstrap icon for the group header, e.g. <c>bi-window-stack</c>.</summary>
    public string? Icon { get; set; }

    /// <summary>Accent colour for the group header icon (any CSS colour).</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Optional regex over the entry name (file name without extension). Named groups
    /// <c>workspace</c> and <c>location</c> split one entry into a workspace/location pair,
    /// so <c>dev-checkout</c> and <c>demo-checkout</c> collapse into one "checkout" card
    /// holding a "dev" and a "demo" location.
    /// <para>
    /// Set <see cref="MatchOn"/> to <see cref="NameMatch.RelativePath"/> to match the path
    /// instead, for the layouts that keep the branch in a directory rather than the name.
    /// </para>
    /// </summary>
    public string? NameRegex { get; set; }

    /// <summary>
    /// Which string <see cref="NameRegex"/> runs against. Defaults to the entry name, which is
    /// what every source written before this existed expects.
    /// </summary>
    public NameMatch MatchOn { get; set; } = NameMatch.Name;

    /// <summary>Location label used when <see cref="NameRegex"/> supplies none.</summary>
    public string DefaultLocationName { get; set; } = "main";

    /// <summary>
    /// Dotted JSON paths tried in order to build a one-line subtitle for each entry,
    /// e.g. <c>settings.description</c> then <c>folders</c>. Only read when the entry
    /// is a JSON/JSONC file. Leave empty to skip reading file contents entirely.
    /// </summary>
    public List<string> DescriptionFrom { get; set; } = new();

    /// <summary>Optional application used by the card's primary Open button.</summary>
    public CustomOpenOption? OpenWith { get; set; }

    /// <summary>Group name actually used on the dashboard.</summary>
    [YamlIgnore]
    public string GroupName => string.IsNullOrWhiteSpace(Group) ? Name : Group;
}

/// <summary>Outcome of scanning one source, surfaced in the UI so failures are visible.</summary>
public class SourceScanResult
{
    public string SourceName { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public bool PathExists { get; set; }
    public int EntriesFound { get; set; }

    /// <summary>
    /// Cards this source has rows on. Not cards it created: two sources sharing a group fold onto
    /// each other's cards, and counting only the ones a source was first to make described the
    /// order the sources are listed in rather than anything about the source.
    /// </summary>
    public int WorkspacesProduced { get; set; }

    public string? Error { get; set; }
}
