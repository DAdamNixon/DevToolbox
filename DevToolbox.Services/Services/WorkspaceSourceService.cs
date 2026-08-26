using System.Text.Json;
using System.Text.RegularExpressions;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Scans the folders listed in Config/workspaceSources.yaml and projects what it finds
/// onto the dashboard as read-only groups. Nothing here knows about VS Code, Visual
/// Studio or any particular directory — the pattern, grouping and open command all come
/// from config, so the same code covers *.code-workspace, *.sln or bare repo folders.
/// <para>
/// The same code also answers <see cref="PreviewAsync"/>, which is what the Scan Folders
/// dialog renders while you type. Sharing <see cref="Collect"/> and <see cref="BuildGroup"/>
/// between the two is the point: a preview computed by a parallel implementation would only
/// be as trustworthy as the last time someone remembered to change both.
/// </para>
/// </summary>
public class WorkspaceSourceService : IWorkspaceSourceService
{
    private const string ConfigKey = "workspaceSources";

    /// <summary>
    /// Entries a preview will look at. A preview runs on every keystroke, and a recursive
    /// <c>*</c> pointed at a drive root is one keystroke away — so it samples rather than
    /// enumerates, and says so when it has.
    /// </summary>
    private const int PreviewEntryCap = 200;

    private readonly IYamlStorageService _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WorkspaceSourceConfig? _config;
    private List<WorkspaceGroup> _groups = new();
    private List<SourceScanResult> _lastScan = new();
    private bool _scanned;

    /// <summary>Maps a discovered location path back to the source that produced it.</summary>
    private Dictionary<string, WorkspaceSource> _sourceByPath = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceSourceService(IYamlStorageService storage)
    {
        _storage = storage;
    }

    public IReadOnlyList<SourceScanResult> LastScan => _lastScan;

    public async Task<WorkspaceSourceConfig> GetConfigAsync()
    {
        if (_config is not null)
        {
            return _config;
        }

        try
        {
            _config = await _storage.LoadAsync<WorkspaceSourceConfig>(ConfigKey) ?? new WorkspaceSourceConfig();
        }
        catch (Exception ex)
        {
            // A hand-edited config that no longer parses must not take the dashboard down.
            Console.WriteLine($"WorkspaceSourceService: could not read {ConfigKey}.yaml — {ex.Message}");
            _config = new WorkspaceSourceConfig();
        }

        return _config;
    }

    public async Task SaveConfigAsync(WorkspaceSourceConfig config)
    {
        await _storage.SaveAsync(ConfigKey, config);
        _config = config;
        _scanned = false;
    }

    string ICachedConfig.ConfigKey => ConfigKey;

    /// <summary>
    /// Drops the loaded sources and the scan built from them, so the next
    /// <see cref="GetGroupsAsync"/> rescans. Same pair <see cref="SaveConfigAsync"/> resets —
    /// a restored file that left a stale scan in place would show the old groups.
    /// </summary>
    public void Invalidate()
    {
        _config = null;
        _scanned = false;
    }

    public async Task<List<WorkspaceGroup>> GetGroupsAsync(bool forceRescan = false)
    {
        if (_scanned && !forceRescan)
        {
            return _groups;
        }

        await _gate.WaitAsync();
        try
        {
            if (_scanned && !forceRescan)
            {
                return _groups;
            }

            if (forceRescan)
            {
                _config = null;
            }

            var config = await GetConfigAsync();
            Scan(config);
            _scanned = true;
        }
        finally
        {
            _gate.Release();
        }

        return _groups;
    }

    public CustomOpenOption? GetOpenOptionFor(WorkspaceLocation location)
    {
        if (string.IsNullOrEmpty(location.Path))
        {
            return null;
        }

        return _sourceByPath.TryGetValue(location.Path, out var source) ? source.OpenWith : null;
    }

    public Task<SourcePreview> PreviewAsync(WorkspaceSource source, CancellationToken cancellationToken = default)
    {
        // Off the UI thread: this is file I/O on a keystroke, and a network share that has gone
        // quiet would otherwise freeze the dialog it is meant to be describing.
        return Task.Run(() => Preview(source, cancellationToken), cancellationToken);
    }

    private static SourcePreview Preview(WorkspaceSource source, CancellationToken cancellationToken)
    {
        var preview = new SourcePreview { GroupName = source.GroupName };

        var root = ExpandPath(source.Path);
        preview.ResolvedPath = root;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return preview;
        }

        preview.PathExists = true;

        var collected = Collect(source, root, PreviewEntryCap, cancellationToken);
        preview.Error = collected.Error;
        preview.RegexError = collected.RegexError;
        preview.EntriesFound = collected.TotalFound;
        preview.Truncated = collected.Truncated;

        cancellationToken.ThrowIfCancellationRequested();

        // The same fold the real scan does, into preview shapes rather than domain models —
        // one workspace per distinct name, one location per entry underneath it.
        var group = BuildGroup(source, root, collected.Entries, id: 0, nextWorkspaceId: () => 0);

        preview.Workspaces = group.Workspaces
            .Select(w => new PreviewWorkspace
            {
                Name = w.Name,
                Locations = w.Locations
                    .Select(l => new PreviewLocation
                    {
                        Name = l.Name,
                        Path = l.Path,
                        Description = l.Description,
                        Entry = BareName(l.Path, source),
                        RegexMatched = !collected.Unmatched.Contains(l.Path)
                    })
                    .ToList()
            })
            .ToList();

        preview.Unmatched = collected.Entries
            .Where(e => !e.RegexMatched)
            .Select(e => e.Bare)
            .ToList();

        return preview;
    }

    private void Scan(WorkspaceSourceConfig config)
    {
        var groups = new List<WorkspaceGroup>();
        var results = new List<SourceScanResult>();
        var pathMap = new Dictionary<string, WorkspaceSource>(StringComparer.OrdinalIgnoreCase);

        // Scanned groups and workspaces live alongside the hand-managed ones, which use
        // positive ids from workspaceGroups.yaml. Counting down from -1 keeps them apart.
        var nextGroupId = -1;
        var nextWorkspaceId = -1;

        foreach (var source in config.Sources.Where(s => s.Enabled))
        {
            var result = new SourceScanResult { SourceName = source.Name };
            results.Add(result);

            try
            {
                var root = ExpandPath(source.Path);
                result.ResolvedPath = root;

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    result.PathExists = false;
                    continue;
                }

                result.PathExists = true;

                var collected = Collect(source, root, limit: null, CancellationToken.None);
                result.EntriesFound = collected.TotalFound;

                if (collected.Error is not null)
                {
                    result.Error = collected.Error;
                    continue;
                }

                var group = groups.FirstOrDefault(g => g.Name.Equals(source.GroupName, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    group = new WorkspaceGroup
                    {
                        Id = nextGroupId--,
                        Name = source.GroupName,
                        SourceName = source.Name,
                        SourcePath = root,
                        SourceIcon = string.IsNullOrWhiteSpace(source.Icon) && string.IsNullOrWhiteSpace(source.Color)
                            ? null
                            : new IconStyle { Icon = source.Icon, Color = source.Color }
                    };
                    groups.Add(group);
                }

                var before = group.Workspaces.Count;
                Fold(group, source, root, collected.Entries, () => nextWorkspaceId--);
                result.WorkspacesProduced = group.Workspaces.Count - before;

                foreach (var entry in collected.Entries)
                {
                    pathMap[entry.Path] = source;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Console.WriteLine($"WorkspaceSourceService: source '{source.Name}' failed — {ex.Message}");
            }
        }

        foreach (var group in groups)
        {
            group.Workspaces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var workspace in group.Workspaces)
            {
                workspace.Locations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        _groups = groups;
        _lastScan = results;
        _sourceByPath = pathMap;
    }

    /// <summary>One entry off disk, with its name already split into workspace and location.</summary>
    private sealed record CollectedEntry(
        string Path,
        string Bare,
        string WorkspaceName,
        string LocationName,
        bool RegexMatched);

    /// <summary>Everything <see cref="Collect"/> found, including the reasons it found less.</summary>
    private sealed class Collected
    {
        public List<CollectedEntry> Entries { get; } = new();

        /// <summary>Paths whose name the regex did not match. Empty when there is no regex.</summary>
        public HashSet<string> Unmatched { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Error { get; set; }

        public string? RegexError { get; set; }

        /// <summary>Entries on disk, which exceeds <see cref="Entries"/> when a limit applied.</summary>
        public int TotalFound { get; set; }

        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Enumerates a source's folder and works out the workspace/location split for each entry.
    /// <paramref name="limit"/> caps how many are kept — null for a real scan, a small number
    /// for a preview that has to return between keystrokes.
    /// </summary>
    private static Collected Collect(WorkspaceSource source, string root, int? limit, CancellationToken cancellationToken)
    {
        var collected = new Collected();
        var regex = BuildRegex(source, out var regexError);
        collected.RegexError = regexError;

        List<string> entries;
        try
        {
            entries = Enumerate(root, source)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            collected.Error = ex.Message;
            return collected;
        }

        collected.TotalFound = entries.Count;

        if (limit is { } cap && entries.Count > cap)
        {
            collected.Truncated = true;
            entries = entries.Take(cap).ToList();
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bare = BareName(entry, source);
            var (workspaceName, locationName, matched) = SplitName(bare, source, regex);

            collected.Entries.Add(new CollectedEntry(entry, bare, workspaceName, locationName, matched));

            if (!matched)
            {
                collected.Unmatched.Add(entry);
            }
        }

        return collected;
    }

    /// <summary>
    /// Folds entries into a group: one workspace per distinct workspace name, one location
    /// under it per entry. Called by both the scan and the preview, which is what keeps the
    /// two showing the same cards.
    /// </summary>
    private static void Fold(
        WorkspaceGroup group,
        WorkspaceSource source,
        string root,
        IEnumerable<CollectedEntry> entries,
        Func<int> nextWorkspaceId)
    {
        foreach (var entry in entries)
        {
            var workspace = group.Workspaces.FirstOrDefault(
                w => w.Name.Equals(entry.WorkspaceName, StringComparison.OrdinalIgnoreCase));

            if (workspace is null)
            {
                workspace = new Workspace
                {
                    Id = nextWorkspaceId(),
                    Name = entry.WorkspaceName,
                    GroupName = group.Name,
                    SourceName = source.Name
                };
                group.Workspaces.Add(workspace);
            }

            workspace.Locations.Add(new WorkspaceLocation
            {
                Name = entry.LocationName,
                Path = entry.Path,
                Root = source.Scan == ScanKind.Directories
                    ? entry.Path
                    : System.IO.Path.GetDirectoryName(entry.Path) ?? root,
                Type = source.Scan == ScanKind.Directories ? LocationType.Folder : LocationType.File,
                Description = ReadDescription(entry.Path, source)
            });
        }
    }

    /// <summary>A standalone group holding the fold, sorted the way the dashboard shows it.</summary>
    private static WorkspaceGroup BuildGroup(
        WorkspaceSource source,
        string root,
        IEnumerable<CollectedEntry> entries,
        int id,
        Func<int> nextWorkspaceId)
    {
        var group = new WorkspaceGroup
        {
            Id = id,
            Name = source.GroupName,
            SourceName = source.Name,
            SourcePath = root
        };

        Fold(group, source, root, entries, nextWorkspaceId);

        group.Workspaces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var workspace in group.Workspaces)
        {
            workspace.Locations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        return group;
    }

    private static IEnumerable<string> Enumerate(string root, WorkspaceSource source)
    {
        var pattern = string.IsNullOrWhiteSpace(source.Pattern) ? "*" : source.Pattern;
        var option = source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return source.Scan == ScanKind.Directories
            ? Directory.EnumerateDirectories(root, pattern, option)
            : Directory.EnumerateFiles(root, pattern, option);
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            return System.IO.Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path being typed is invalid for most of the time it takes to type it, and the
            // preview asks about it on every keystroke. "Folder not found" is the honest answer.
            return string.Empty;
        }
    }

    private static Regex? BuildRegex(WorkspaceSource source, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(source.NameRegex))
        {
            return null;
        }

        try
        {
            return new Regex(source.NameRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            Console.WriteLine($"WorkspaceSourceService: source '{source.Name}' has an invalid nameRegex — {ex.Message}");
            return null;
        }
    }

    /// <summary>The part of an entry a name pattern is matched against.</summary>
    private static string BareName(string entry, WorkspaceSource source) =>
        source.Scan == ScanKind.Directories
            ? new DirectoryInfo(entry).Name
            : System.IO.Path.GetFileNameWithoutExtension(entry);

    /// <summary>
    /// Derives the workspace/location pair for one entry. Without a regex every entry is
    /// its own workspace; with one, entries sharing a "workspace" capture merge into a
    /// single card holding one location each.
    /// </summary>
    private static (string Workspace, string Location, bool Matched) SplitName(
        string bare, WorkspaceSource source, Regex? regex)
    {
        var fallbackLocation = string.IsNullOrWhiteSpace(source.DefaultLocationName)
            ? "main"
            : source.DefaultLocationName;

        if (regex is null)
        {
            // No regex is not a failed match: one card per entry is the intended result, and
            // flagging it would fill a preview with warnings about working as configured.
            return (bare, fallbackLocation, true);
        }

        var match = regex.Match(bare);
        if (!match.Success)
        {
            return (bare, fallbackLocation, false);
        }

        var workspace = match.Groups["workspace"];
        var location = match.Groups["location"];

        return (
            workspace.Success && workspace.Value.Length > 0 ? workspace.Value : bare,
            location.Success && location.Value.Length > 0 ? location.Value : fallbackLocation,
            true);
    }

    /// <summary>
    /// Pulls a subtitle out of a JSON/JSONC entry using the dotted paths on the source.
    /// A path landing on an array yields a count ("16 folders"), which is what makes
    /// .code-workspace files readable at a glance.
    /// </summary>
    private static string? ReadDescription(string entry, WorkspaceSource source)
    {
        if (source.DescriptionFrom.Count == 0 || source.Scan == ScanKind.Directories)
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(entry);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            foreach (var path in source.DescriptionFrom)
            {
                var value = ResolveJsonPath(document.RootElement, path);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Not JSON, or unreadable — a missing subtitle is not worth failing the scan over.
        }

        return null;
    }

    private static string? ResolveJsonPath(JsonElement root, string dottedPath)
    {
        var current = root;

        foreach (var segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var child))
            {
                return null;
            }

            current = child;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.Array => Pluralize(current.GetArrayLength(), dottedPath),
            _ => null
        };
    }

    private static string Pluralize(int count, string dottedPath)
    {
        var noun = dottedPath.Split('.').Last();
        if (count == 1 && noun.EndsWith('s'))
        {
            noun = noun[..^1];
        }

        return $"{count} {noun}";
    }
}
