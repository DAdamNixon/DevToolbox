using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <inheritdoc cref="ISavedQueryService"/>
public class SavedQueryService : ISavedQueryService
{
    private const string StorageFile = "saved_queries";

    /// <summary>
    /// Group first, then name, and both case-insensitively: the picker renders headings in this
    /// order, and "checkout" sorting into a different place from "Checkout" would look like two
    /// groups. <see cref="StringComparer.OrdinalIgnoreCase"/> rather than the current culture so a
    /// machine's locale cannot reorder a shared config file.
    /// </summary>
    private static readonly StringComparer NameOrder = StringComparer.OrdinalIgnoreCase;

    private readonly IYamlStorageService _yamlStorage;

    public SavedQueryService(IYamlStorageService yamlStorage)
    {
        _yamlStorage = yamlStorage;
    }

    public async Task<List<SavedQuery>> GetAllAsync()
    {
        var config = await _yamlStorage.LoadAsync<SavedQueryConfig>(StorageFile);
        var queries = config?.Queries ?? new List<SavedQuery>();

        // A file that has been hand-edited — which these are meant to be — can arrive without ids.
        // Filling one in on read keeps the query usable, and it becomes a real stored id the next
        // time anything saves.
        foreach (var query in queries.Where(q => string.IsNullOrWhiteSpace(q.Id)))
            query.Id = DerivedId(query.Group, query.Name);

        return Sorted(queries);
    }

    public async Task<SavedQuery> SaveAsync(SavedQuery query)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

        var name = (query.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("A saved query needs a name.", nameof(query));

        var sql = (query.Sql ?? "").Trim();
        if (sql.Length == 0)
            throw new ArgumentException("There is no SQL to save.", nameof(query));

        var group = (query.Group ?? "").Trim();
        var queries = await GetAllAsync();

        var existing = string.IsNullOrWhiteSpace(query.Id)
            ? null
            : queries.FirstOrDefault(q => q.Id == query.Id);

        if (queries.Any(q => q != existing
                             && NameOrder.Equals(q.Group, group)
                             && NameOrder.Equals(q.Name, name)))
        {
            var where = group.Length == 0 ? "the ungrouped queries" : $"the '{group}' group";
            throw new InvalidOperationException($"A query called '{name}' is already in {where}.");
        }

        var stored = existing ?? new SavedQuery { Id = NewId() };
        stored.Name = name;
        stored.Group = group;
        stored.Sql = sql;
        stored.Description = string.IsNullOrWhiteSpace(query.Description) ? null : query.Description.Trim();
        stored.Template = string.IsNullOrWhiteSpace(query.Template) ? null : query.Template.Trim();
        stored.UpdatedUtc = DateTime.UtcNow;

        if (existing is null) queries.Add(stored);

        await WriteAsync(queries);
        return stored;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var queries = await GetAllAsync();
        var remaining = queries.Where(q => q.Id != id).ToList();
        if (remaining.Count == queries.Count) return false;

        await WriteAsync(remaining);
        return true;
    }

    public async Task<List<string>> GetGroupsAsync()
    {
        var queries = await GetAllAsync();
        return queries
            .Select(q => (q.Group ?? "").Trim())
            .Where(g => g.Length > 0)
            .Distinct(NameOrder)
            .OrderBy(g => g, NameOrder)
            .ToList();
    }

    public async Task<int> RenameGroupAsync(string from, string to)
    {
        var source = (from ?? "").Trim();
        var target = (to ?? "").Trim();
        if (NameOrder.Equals(source, target)) return 0;

        var queries = await GetAllAsync();
        var moving = queries.Where(q => NameOrder.Equals((q.Group ?? "").Trim(), source)).ToList();
        if (moving.Count == 0) return 0;

        // Renaming onto an existing group merges the two, so the same collision that blocks a save
        // has to be checked here as well — otherwise the merge is how you end up with two rows the
        // picker draws identically.
        var staying = queries.Except(moving).ToList();
        var clash = moving.FirstOrDefault(m => staying.Any(s =>
            NameOrder.Equals((s.Group ?? "").Trim(), target) && NameOrder.Equals(s.Name, m.Name)));

        if (clash is not null)
        {
            var where = target.Length == 0 ? "the ungrouped queries" : $"'{target}'";
            throw new InvalidOperationException(
                $"'{clash.Name}' cannot move to {where} — a query of that name is already there.");
        }

        foreach (var query in moving) query.Group = target;

        await WriteAsync(queries);
        return moving.Count;
    }

    private Task WriteAsync(List<SavedQuery> queries) =>
        _yamlStorage.SaveAsync(StorageFile, new SavedQueryConfig { Queries = Sorted(queries) });

    /// <summary>
    /// Sorted on the way out <em>and</em> on the way in, so the file on disk reads in the same order
    /// the picker does. These are meant to be hand-editable, and a list that reorders itself
    /// invisibly on every save makes a diff of one useless.
    /// </summary>
    private static List<SavedQuery> Sorted(IEnumerable<SavedQuery> queries) =>
        queries
            .OrderBy(q => (q.Group ?? "").Trim(), NameOrder)
            .ThenBy(q => q.Name, NameOrder)
            .ToList();

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The id a query gets when the file did not give it one — derived from its group and name
    /// rather than random.
    /// <para>
    /// It has to be <em>stable across reads</em>, not merely unique: <see cref="SaveAsync"/> begins
    /// by re-reading the store and finds the row it is updating by id, so a fresh GUID each time
    /// would make every hand-written query impossible to edit — the save would fail to match it and
    /// then refuse itself as a duplicate name. Caught by
    /// <c>A_hand_written_query_with_no_id_still_loads_and_gets_one</c>.
    /// </para>
    /// </summary>
    private static string DerivedId(string? group, string? name)
    {
        var key = $"{(group ?? "").Trim().ToLowerInvariant()}\u0000{(name ?? "").Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
