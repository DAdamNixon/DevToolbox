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
/// </summary>
public class WorkspaceSourceService : IWorkspaceSourceService
{
    private const string ConfigKey = "workspaceSources";

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

                var entries = Enumerate(root, source).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
                result.EntriesFound = entries.Count;

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

                var regex = BuildRegex(source);

                foreach (var entry in entries)
                {
                    var (workspaceName, locationName) = SplitName(entry, source, regex);

                    var workspace = group.Workspaces.FirstOrDefault(
                        w => w.Name.Equals(workspaceName, StringComparison.OrdinalIgnoreCase));

                    if (workspace is null)
                    {
                        workspace = new Workspace
                        {
                            Id = nextWorkspaceId--,
                            Name = workspaceName,
                            GroupName = group.Name,
                            SourceName = source.Name
                        };
                        group.Workspaces.Add(workspace);
                        result.WorkspacesProduced++;
                    }

                    workspace.Locations.Add(new WorkspaceLocation
                    {
                        Name = locationName,
                        Path = entry,
                        Root = source.Scan == ScanKind.Directories
                            ? entry
                            : System.IO.Path.GetDirectoryName(entry) ?? root,
                        Type = source.Scan == ScanKind.Directories ? LocationType.Folder : LocationType.File,
                        Description = ReadDescription(entry, source)
                    });

                    pathMap[entry] = source;
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

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return System.IO.Path.GetFullPath(expanded);
    }

    private static Regex? BuildRegex(WorkspaceSource source)
    {
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
            Console.WriteLine($"WorkspaceSourceService: source '{source.Name}' has an invalid nameRegex — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Derives the workspace/location pair for one entry. Without a regex every entry is
    /// its own workspace; with one, entries sharing a "workspace" capture merge into a
    /// single card holding one location each.
    /// </summary>
    private static (string Workspace, string Location) SplitName(string entry, WorkspaceSource source, Regex? regex)
    {
        var bare = source.Scan == ScanKind.Directories
            ? new DirectoryInfo(entry).Name
            : System.IO.Path.GetFileNameWithoutExtension(entry);

        var fallbackLocation = string.IsNullOrWhiteSpace(source.DefaultLocationName)
            ? "main"
            : source.DefaultLocationName;

        if (regex is null)
        {
            return (bare, fallbackLocation);
        }

        var match = regex.Match(bare);
        if (!match.Success)
        {
            return (bare, fallbackLocation);
        }

        var workspace = match.Groups["workspace"];
        var location = match.Groups["location"];

        return (
            workspace.Success && workspace.Value.Length > 0 ? workspace.Value : bare,
            location.Success && location.Value.Length > 0 ? location.Value : fallbackLocation);
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
