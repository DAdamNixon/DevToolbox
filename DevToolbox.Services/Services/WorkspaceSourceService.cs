using System.IO.Enumeration;
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
/// The same code also answers <see cref="PreviewAsync"/>, which is what the Smart Folders
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

            // Off the calling thread, which on the dashboard's first load is the renderer's.
            // A scan is one directory stat per folder under every source; over a whole source
            // tree that is tens of thousands of them and takes seconds, and Scan is synchronous
            // throughout — so running it here freezes the window that is waiting for it. Same
            // reason PreviewAsync does the same thing.
            await Task.Run(() => Scan(config));

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

        // What the regex was handed, per entry. The dialog shows this rather than the file name:
        // with MatchOn: RelativePath the regex never saw the file name, so showing it would be
        // showing the wrong string to whoever is trying to work out why the pattern missed.
        var subjectByPath = collected.Entries
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Subject, StringComparer.OrdinalIgnoreCase);

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
                        Entry = subjectByPath.TryGetValue(l.Path, out var subject) ? subject : l.Path,
                        RegexMatched = !collected.Unmatched.Contains(l.Path),
                        Segments = BuildSegments(
                            subjectByPath.TryGetValue(l.Path, out var s) ? s : l.Path,
                            collected.Regex)
                    })
                    .ToList()
            })
            .ToList();

        preview.Unmatched = collected.Entries
            .Where(e => !e.RegexMatched)
            .Select(e => e.Subject)
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

                Fold(group, source, root, collected.Entries, () => nextWorkspaceId--);

                // Cards this source has rows on — not cards it was the first to create. When two
                // sources share a group the second one mostly lands on cards the first already
                // made, and counting only the new ones reported "1 card" for a source carrying 58
                // rows. It also disagreed with the Scan Folders preview, which has no group to
                // add to and always counted this way, so one list showed two different meanings.
                result.WorkspacesProduced = collected.Entries
                    .Select(e => e.WorkspaceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

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
    /// <param name="Bare">The entry's own name, extension dropped. The card name when nothing matches.</param>
    /// <param name="Subject">
    /// The string the name regex was run against — <paramref name="Bare"/>, or the path below the
    /// scanned folder. Kept so the preview can show what the regex saw rather than what it produced;
    /// with <see cref="NameMatch.RelativePath"/> those are not the same string, and a regex is
    /// debugged against the former.
    /// </param>
    private sealed record CollectedEntry(
        string Path,
        string Bare,
        string Subject,
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

        /// <summary>
        /// The compiled name pattern, or null when there is none or it would not compile. Handed
        /// back so the preview can re-run it per entry to find out where the captures landed,
        /// without compiling it a second time or guessing at what Collect decided.
        /// </summary>
        public Regex? Regex { get; set; }

        /// <summary>
        /// Entries looked at. Equal to <see cref="Entries"/>: when a limit applied the walk stopped
        /// there, so how many more there were was never established — see <see cref="Truncated"/>.
        /// </summary>
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
        collected.Regex = regex;

        List<string> entries;
        try
        {
            var found = Enumerate(root, source);

            // One past the cap rather than all of it. A preview runs between keystrokes and a
            // recursive source covers thousands of folders, so finding out exactly how many
            // entries are down there costs the whole walk — every keystroke. One extra entry is
            // enough to know there are more, which is all the dialog needs to say.
            entries = limit is { } cap ? found.Take(cap + 1).ToList() : found.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            collected.Error = ex.Message;
            return collected;
        }

        if (limit is { } capped && entries.Count > capped)
        {
            collected.Truncated = true;
            entries.RemoveAt(entries.Count - 1);
        }

        collected.TotalFound = entries.Count;
        entries.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bare = BareName(entry, source);
            var subject = MatchSubject(entry, root, bare, source);
            var (workspaceName, locationName, matched) = SplitName(subject, bare, source, regex);

            collected.Entries.Add(new CollectedEntry(entry, bare, subject, workspaceName, locationName, matched));

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
                Type = TypeOf(entry.Path, source.Scan),
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

    /// <summary>
    /// Enumerates a source's folder, skipping the directories its <see cref="WorkspaceSource.Exclude"/>
    /// names <em>instead of descending into them</em>. Filtering after the fact would give the same
    /// cards for 35x the walk: a website working copy is 78,000 directories, of which 3,900 are not
    /// <c>bin</c>, <c>obj</c>, <c>node_modules</c> or <c>.vs</c> — and a preview runs on a keystroke.
    /// <para>
    /// Hand-driven rather than <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// because that overload has no way to prune, and because it aborts the whole walk on the first
    /// directory it cannot read — which over a tree this size is a scan that silently returns half
    /// of what is there.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Enumerate(string root, WorkspaceSource source)
    {
        var pattern = NormalizePattern(source.Pattern);
        var excludes = source.Exclude
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => NormalizePattern(e))
            .ToArray();

        var wantDirectories = source.Scan == ScanKind.Directories;

        return new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = source.Recursive,
                IgnoreInaccessible = true,

                // What the SearchOption overloads use, so hidden and system entries keep showing up.
                // Leaving them out belongs in Exclude, where config says so out loud.
                AttributesToSkip = 0
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                entry.IsDirectory == wantDirectories
                && FileSystemName.MatchesWin32Expression(pattern, entry.FileName)
                && !IsExcluded(entry.FileName, excludes),

            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsExcluded(entry.FileName, excludes)
        };
    }

    /// <summary>
    /// Whether one path segment is excluded — a directory about to be descended into, or a matched
    /// entry itself. Uses the same globs as <see cref="WorkspaceSource.Pattern"/>, so an exclude
    /// reads the way the pattern beside it does.
    /// </summary>
    private static bool IsExcluded(ReadOnlySpan<char> segment, string[] excludes)
    {
        foreach (var exclude in excludes)
        {
            if (FileSystemName.MatchesWin32Expression(exclude, segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A glob in the form the matcher wants, normalized the way <see cref="Directory"/> normalizes
    /// it. Every existing source's pattern has to keep matching exactly what it matched before the
    /// enumeration moved off the framework's own overload, and this is what makes that true.
    /// </summary>
    private static string NormalizePattern(string? pattern)
    {
        var expression = pattern?.Trim();

        if (string.IsNullOrEmpty(expression) || expression is "." or "*" or "*.*")
        {
            return "*";
        }

        return FileSystemName.TranslateWin32Expression(expression);
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

    /// <summary>An entry's own name, extension dropped.</summary>
    private static string BareName(string entry, WorkspaceSource source) =>
        source.Scan == ScanKind.Directories
            ? new DirectoryInfo(entry).Name
            : System.IO.Path.GetFileNameWithoutExtension(entry);

    /// <summary>
    /// The string the name pattern is matched against. Defaults to the entry's own name, which is
    /// enough when the copies sit side by side in one folder — <c>dev-checkout</c> beside
    /// <c>demo-checkout</c>.
    /// <para>
    /// It is not enough when the branch is a directory instead: under
    /// <c>elliottelectric_com\development\wwwroot\Checkout\</c> and
    /// <c>…\demo\wwwroot\Checkout\</c> both leaves are called <c>Checkout.sln</c>, so no regex over
    /// the name can tell them apart and both would land on one card as two locations named the
    /// same thing. <see cref="NameMatch.RelativePath"/> hands the regex the path below the scanned
    /// folder instead, extension kept so it can also pin <c>\.sln$</c>.
    /// </para>
    /// </summary>
    private static string MatchSubject(string entry, string root, string bare, WorkspaceSource source) =>
        source.MatchOn == NameMatch.RelativePath
            ? System.IO.Path.GetRelativePath(root, entry)
            : bare;

    /// <summary>
    /// The type a scanned location gets, inferred the same way the Add Location dialog infers it
    /// for a hand-typed path — so a scanned solution and a hand-added one are the same kind of thing
    /// rather than differing by how they were found.
    /// </summary>
    private static LocationType TypeOf(string path, ScanKind scan)
    {
        if (scan == ScanKind.Directories)
        {
            return LocationType.Folder;
        }

        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sln" or ".slnx" or ".slnf" => LocationType.Solution,
            ".csproj" or ".vbproj" or ".fsproj" => LocationType.Project,
            _ => LocationType.File
        };
    }

    /// <summary>
    /// Derives the workspace/location pair for one entry. Without a regex every entry is
    /// its own workspace; with one, entries sharing a "workspace" capture merge into a
    /// single card holding one location each.
    /// <para>
    /// The regex runs against <paramref name="subject"/>, but a miss falls back to
    /// <paramref name="bare"/> — the entry's own name is the only sane card name, and a relative
    /// path is not one.
    /// </para>
    /// </summary>
    private static (string Workspace, string Location, bool Matched) SplitName(
        string subject, string bare, WorkspaceSource source, Regex? regex)
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

        var match = regex.Match(subject);
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
    /// Cuts <paramref name="subject"/> into the runs the pattern captured, for the preview's
    /// highlighting. Everything outside a capture comes back roled
    /// <see cref="CaptureRole.None"/>, so the pieces always reassemble into the original string.
    /// <para>
    /// Captures are taken in positional order rather than by name, because .NET allows the same
    /// group name on both sides of an alternation and either one can be the one that matched.
    /// Overlapping captures — a <c>workspace</c> nested inside a <c>location</c> — would be
    /// ambiguous to colour, so a capture starting inside one already taken is skipped.
    /// </para>
    /// </summary>
    private static List<PreviewSegment> BuildSegments(string subject, Regex? regex)
    {
        var whole = new List<PreviewSegment>
        {
            new() { Text = subject, Role = CaptureRole.None }
        };

        if (regex is null || string.IsNullOrEmpty(subject))
        {
            return whole;
        }

        var match = regex.Match(subject);
        if (!match.Success)
        {
            return whole;
        }

        var captures = new List<(int Start, int Length, CaptureRole Role)>();

        foreach (var (name, role) in new[]
                 {
                     ("workspace", CaptureRole.Workspace),
                     ("location", CaptureRole.Location)
                 })
        {
            var group = match.Groups[name];
            if (group.Success && group.Length > 0)
            {
                captures.Add((group.Index, group.Length, role));
            }
        }

        if (captures.Count == 0)
        {
            return whole;
        }

        captures.Sort((a, b) => a.Start.CompareTo(b.Start));

        var segments = new List<PreviewSegment>();
        var at = 0;

        foreach (var capture in captures)
        {
            if (capture.Start < at)
            {
                // Overlaps one already emitted. Nothing sensible to paint, so it keeps the
                // colour of whichever capture got there first.
                continue;
            }

            if (capture.Start > at)
            {
                segments.Add(new PreviewSegment
                {
                    Text = subject[at..capture.Start],
                    Role = CaptureRole.None
                });
            }

            segments.Add(new PreviewSegment
            {
                Text = subject.Substring(capture.Start, capture.Length),
                Role = capture.Role
            });

            at = capture.Start + capture.Length;
        }

        if (at < subject.Length)
        {
            segments.Add(new PreviewSegment { Text = subject[at..], Role = CaptureRole.None });
        }

        return segments;
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
