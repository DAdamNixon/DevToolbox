using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <inheritdoc cref="ILogConfigService"/>
public class LogConfigService : ILogConfigService
{
    private const string IndexFile = "log_templates_index";
    private const string LocationsFile = "log_paths";

    private readonly IYamlStorageService _yamlStorage;

    public LogConfigService(IYamlStorageService yamlStorage)
    {
        _yamlStorage = yamlStorage;
    }

    public async Task<List<LogTemplateIndexEntry>> GetTemplatesAsync()
    {
        var config = await _yamlStorage.LoadAsync<LogTemplateIndexConfig>(IndexFile);
        return config?.Templates ?? new List<LogTemplateIndexEntry>();
    }

    public Task<LogTemplate?> LoadTemplateAsync(string file) =>
        _yamlStorage.LoadAsync<LogTemplate>(BaseName(file));

    public async Task<LogTemplateIndexEntry> SaveTemplateAsync(string? existingFile, LogTemplate template)
    {
        var templates = await GetTemplatesAsync();
        var name = template.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("A template needs a name before it can be saved.", nameof(template));

        template.Name = name;
        Tidy(template);

        var entry = string.IsNullOrWhiteSpace(existingFile)
            ? null
            : templates.FirstOrDefault(t => FileMatches(t.File, existingFile!));

        // The file name is derived once, on create, and then left alone. Renaming the file to follow
        // the template's name would break every other template that inherits from it — `inherits`
        // names the *file*, not the display name — so a rename only ever touches the index entry.
        var file = entry?.File
            ?? (string.IsNullOrWhiteSpace(existingFile)
                ? UniqueFileName(name, templates)
                : BaseName(existingFile!) + ".yaml");

        await _yamlStorage.SaveAsync(BaseName(file), template);

        if (entry is null)
        {
            entry = new LogTemplateIndexEntry { Name = name, File = file };
            templates.Add(entry);
        }
        else
        {
            entry.Name = name;
        }

        await _yamlStorage.SaveAsync(IndexFile, new LogTemplateIndexConfig { Templates = templates });

        return entry;
    }

    public async Task DeleteTemplateAsync(string file)
    {
        var templates = await GetTemplatesAsync();
        var remaining = templates.Where(t => !FileMatches(t.File, file)).ToList();

        if (remaining.Count != templates.Count)
            await _yamlStorage.SaveAsync(IndexFile, new LogTemplateIndexConfig { Templates = remaining });

        await _yamlStorage.DeleteAsync(BaseName(file));
    }

    public async Task<List<string>> FindTemplatesInheritingAsync(string file)
    {
        var wanted = BaseName(file);
        var dependents = new List<string>();

        foreach (var entry in await GetTemplatesAsync())
        {
            if (FileMatches(entry.File, file)) continue;

            LogTemplate? template;
            try
            {
                template = await LoadTemplateAsync(entry.File);
            }
            catch (InvalidOperationException)
            {
                // A template that will not parse cannot be shown to depend on anything. It is
                // already broken, and saying so is not this method's job.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(template?.Inherits) &&
                string.Equals(BaseName(template!.Inherits!), wanted, StringComparison.OrdinalIgnoreCase))
            {
                dependents.Add(string.IsNullOrWhiteSpace(entry.Name) ? entry.File : entry.Name);
            }
        }

        return dependents;
    }

    public async Task<List<LogLocation>> GetLocationsAsync()
    {
        var config = await _yamlStorage.LoadAsync<LogLocationConfig>(LocationsFile);
        return config?.LogLocations ?? new List<LogLocation>();
    }

    public Task SaveLocationsAsync(IReadOnlyList<LogLocation> locations)
    {
        var tidied = locations
            .Select(l => new LogLocation
            {
                Name = l.Name?.Trim() ?? "",
                Path = l.Path?.Trim() ?? "",
                // Empty and null both mean "no discovery for this location", and null is what the
                // rest of the code checks for; storing "" would put a `namePattern:` line in the
                // YAML that says nothing.
                NamePattern = string.IsNullOrWhiteSpace(l.NamePattern) ? null : l.NamePattern!.Trim()
            })
            .Where(l => l.Name.Length > 0 || l.Path.Length > 0)
            .ToList();

        return _yamlStorage.SaveAsync(LocationsFile, new LogLocationConfig { LogLocations = tidied });
    }

    /// <summary>
    /// Trims the strings and drops the rows the editor leaves behind — a blank column added and
    /// never filled in, a sort row whose column was cleared. Saving those would put empty entries in
    /// the YAML that the ingest would then try to make SQL columns out of.
    /// </summary>
    private static void Tidy(LogTemplate template)
    {
        var extension = template.Extension?.Trim() ?? "";
        template.Extension = extension.Length > 0 ? extension : ".txt";
        template.Inherits = string.IsNullOrWhiteSpace(template.Inherits) ? null : BaseName(template.Inherits!);

        template.Columns = (template.Columns ?? new List<string>())
            .Select(c => c?.Trim() ?? "")
            .Where(c => c.Length > 0)
            .ToList();

        var sort = (template.Sort ?? new List<SortColumn>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Column))
            .Select(s => new SortColumn
            {
                Column = s.Column.Trim(),
                Direction = string.Equals(s.Direction, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc"
            })
            .ToList();

        // Null rather than an empty list: the ingest treats "no sort configured" as "fall back to
        // whatever the caller asked for", and an empty `sort:` key reads as the same thing without
        // looking like it.
        template.Sort = sort.Count > 0 ? sort : null;
    }

    /// <summary>
    /// A file name derived from the template's name — "EES Net Logs" becomes
    /// <c>EES_Net_Logs.yaml</c> — with a numeric suffix if that is already taken. Sanitised
    /// aggressively rather than cleverly: this is a file name in a config folder, and a template
    /// someone calls <c>../../etc</c> must not become a path.
    /// </summary>
    private static string UniqueFileName(string templateName, IEnumerable<LogTemplateIndexEntry> existing)
    {
        var sb = new StringBuilder();
        foreach (var ch in templateName)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        var stem = sb.ToString().Trim('_');
        while (stem.Contains("__")) stem = stem.Replace("__", "_");
        if (stem.Length == 0) stem = "Template";
        if (stem.Length > 60) stem = stem.Substring(0, 60);

        var taken = new HashSet<string>(existing.Select(e => BaseName(e.File)), StringComparer.OrdinalIgnoreCase);
        var candidate = stem;
        for (var n = 2; taken.Contains(candidate); n++)
            candidate = stem + "_" + n;

        return candidate + ".yaml";
    }

    /// <summary>
    /// A file name with no extension and no directory. Index entries carry <c>.yaml</c> and
    /// <c>inherits</c> does not, and the storage layer wants neither, so everything goes through
    /// here rather than each caller guessing which form it was handed.
    /// </summary>
    private static string BaseName(string file) => Path.GetFileNameWithoutExtension(file.Trim());

    private static bool FileMatches(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(BaseName(a!), BaseName(b), StringComparison.OrdinalIgnoreCase);
}
