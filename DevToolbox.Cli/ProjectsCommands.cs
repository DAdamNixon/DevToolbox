using System.CommandLine;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Microsoft.Extensions.Configuration;

namespace DevToolbox.Cli;

/// <summary>
/// <c>projects</c>: the Projects tab from a terminal — the same hand-made groups and Smart Folder
/// scans, the same abbreviation search, and the same Open.
/// </summary>
internal static class ProjectsCommands
{
    public static Command Build()
    {
        var query = new Argument<string?>("query") { Description = "Name, abbreviation or alias, as typed in the Projects search box. Omit to list everything.", Arity = ArgumentArity.ZeroOrOne };
        var json = Cli.JsonOption();

        var list = new Command("projects", "List projects, or find them by name or abbreviation (persman finds PersonnelManagement).")
        {
            query, json,
        };
        list.SetAction((result, _) => SearchAsync(result.GetValue(query), result.GetValue(json)));

        var target = new Argument<string>("query") { Description = "The project to open. Must match one project, or one exactly by name." };
        var location = new Option<string?>("--location", "-l") { Description = "Which of the project's locations, by name. Default: the first." };
        var how = new Option<string>("--in") { Description = "app (the Open button's program), folder (the file manager), terminal, or code (VS Code).", DefaultValueFactory = _ => "app" };
        how.AcceptOnlyFromAmong("app", "folder", "terminal", "code");

        var open = new Command("open", "Open a project, the way its Open button does.") { target, location, how };
        open.SetAction((result, _) => OpenAsync(result.GetValue(target)!, result.GetValue(location), result.GetValue(how)!));

        list.Subcommands.Add(open);
        return list;
    }

    private sealed record Found(string Group, Workspace Workspace, int Score);

    private sealed class Services
    {
        public Services()
        {
            Yaml = new YamlStorageService();
            System = Platform.SystemService(PowerShell);
            Workspaces = new WorkspaceService(Yaml, PowerShell, System, new ConfigurationBuilder().Build());
            Sources = new WorkspaceSourceService(Yaml);
            Layout = new DashboardLayoutService(Yaml);
            Handlers = new OpenHandlerService(Yaml);
        }

        public PowerShellService PowerShell { get; } = new();
        public YamlStorageService Yaml { get; }
        public ISystemService System { get; }
        public WorkspaceService Workspaces { get; }
        public WorkspaceSourceService Sources { get; }
        public DashboardLayoutService Layout { get; }
        public OpenHandlerService Handlers { get; }
    }

    /// <summary>
    /// Both halves of the Projects tab — saved groups, then the Smart Folder scans — with every card
    /// that matches, best match first. A group whose own name matches keeps all its cards, as on
    /// the tab.
    /// </summary>
    private static async Task<List<Found>> FindAsync(Services services, string? query)
    {
        await services.Layout.GetAsync();

        // Customize is the tab's own view of a group: renamed, merged and hidden cards applied.
        var groups = (await services.Workspaces.GetWorkspaceGroupsAsync())
            .Concat(await services.Sources.GetGroupsAsync())
            .Where(g => !services.Layout.IsHidden(g.Name))
            .Select(g => services.Layout.Customize(g));

        var found = new List<Found>();
        foreach (var group in groups)
        {
            var groupMatches = !string.IsNullOrWhiteSpace(query)
                && WorkspaceSearch.MatchesName(query, group.Name, services.Layout.AliasesFor(AliasScope.Group, group.Name));

            foreach (var workspace in group.Workspaces)
            {
                var aliases = services.Layout.AliasesFor(AliasScope.Workspace, workspace.Name);
                if (groupMatches
                    || WorkspaceSearch.MatchesName(query, workspace.Name, aliases)
                    || workspace.Locations.Any(l => WorkspaceSearch.MatchesPath(query, l.Path)))
                {
                    found.Add(new Found(group.Name, workspace, Rank(query, workspace.Name, aliases)));
                }
            }
        }

        return found
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Workspace.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An exact name beats everything, then the fuzzy score of the name or its best alias.</summary>
    private static int Rank(string? query, string name, IEnumerable<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (string.Equals(query.Trim(), name, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;

        return aliases.Select(a => FuzzyMatch.Score(query, a)).Append(FuzzyMatch.Score(query, name)).Max();
    }

    private static async Task<int> SearchAsync(string? query, bool json)
    {
        var found = await FindAsync(new Services(), query);

        if (json)
        {
            Output.WriteJson(found.Select(f => new
            {
                group = f.Group,
                name = f.Workspace.Name,
                source = f.Workspace.SourceName,
                locations = f.Workspace.Locations.Select(l => new { name = l.Name, path = l.Path, type = l.Type.ToString() }),
            }));
            return 0;
        }

        if (found.Count == 0)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(query) ? "No projects yet. Add a Smart Folder or a group in the app." : $"Nothing matches \"{query}\".");
            return string.IsNullOrWhiteSpace(query) ? 0 : 2;
        }

        Output.WriteTable(
            ["Project", "Group", "Location", "Path"],
            found.SelectMany(f => f.Workspace.Locations.Select((l, i) => (IReadOnlyList<string>)
                [i == 0 ? f.Workspace.Name : "", i == 0 ? f.Group : "", l.Name, l.Path])),
            widthCap: 0);
        return 0;
    }

    private static async Task<int> OpenAsync(string query, string? locationName, string how)
    {
        var services = new Services();
        var found = await FindAsync(services, query);

        // One project, or one whose name is exactly what was typed. Anything else is a guess, and
        // opening the wrong project is worse than asking.
        var chosen = found.Count == 1 ? found[0]
            : found.FirstOrDefault(f => f.Score == int.MaxValue);

        if (chosen is null)
        {
            if (found.Count == 0) return Output.Fail($"Nothing matches \"{query}\".");

            Console.Error.WriteLine($"\"{query}\" matches {found.Count} projects. Be more specific, or use the exact name:");
            foreach (var f in found.Take(15)) Console.Error.WriteLine($"  {f.Workspace.Name}  ({f.Group})");
            return 2;
        }

        var workspace = chosen.Workspace;
        var location = locationName is null
            ? workspace.Locations.FirstOrDefault()
            : workspace.Locations.FirstOrDefault(l => string.Equals(l.Name, locationName, StringComparison.OrdinalIgnoreCase));

        if (location is null)
        {
            return Output.Fail(locationName is null
                ? $"{workspace.Name} has no locations."
                : $"{workspace.Name} has no location \"{locationName}\". It has: {string.Join(", ", workspace.Locations.Select(l => l.Name))}.");
        }

        var result = how switch
        {
            "folder" => await services.Workspaces.OpenLocationInExplorerAsync(location),
            "terminal" => await services.Workspaces.OpenLocationInTerminalAsync(location),
            "code" => await services.Workspaces.OpenLocationInVsCodeAsync(location),
            _ => await OpenLikeTheButtonAsync(services, workspace, location),
        };

        if (!result.Success) return Output.Fail(result.Error ?? "Could not open it.");

        Console.Error.WriteLine($"Opened {workspace.Name} ({location.Name}): {location.Path}");
        return 0;
    }

    /// <summary>
    /// The Open button's order: an app named by the Smart Folder, then the first matching handler in
    /// openHandlers.yaml, then the desktop's own association.
    /// </summary>
    private static async Task<OpenResult> OpenLikeTheButtonAsync(Services services, Workspace workspace, WorkspaceLocation location)
    {
        await services.Handlers.GetConfigAsync();
        await services.Sources.GetConfigAsync();

        var openWith = services.Sources.GetOpenOptionFor(location) ?? services.Handlers.HandlerFor(location.Path);

        return openWith is not null
            ? await services.System.OpenWithCustomAppAsync(location.Path, openWith)
            : await services.Workspaces.OpenWorkspaceLocationAsync(workspace, location);
    }
}
