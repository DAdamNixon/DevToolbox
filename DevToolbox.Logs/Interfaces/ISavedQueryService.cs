using System.Collections.Generic;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// The Log Viewer's saved advanced-mode queries: read, write, group.
/// <para>
/// Separate from <see cref="ILogConfigService"/> — that owns how logs are <em>parsed</em>
/// (templates and locations), this owns what is <em>asked</em> of them once they are in the table.
/// One YAML file, <c>saved_queries.yaml</c>, in the same config folder as the rest.
/// </para>
/// </summary>
public interface ISavedQueryService
{
    /// <summary>Every saved query, ordered by group then name — the order the picker renders.</summary>
    Task<List<SavedQuery>> GetAllAsync();

    /// <summary>
    /// Writes the query, assigning an id and stamping <see cref="SavedQuery.UpdatedUtc"/> when it is
    /// new. Returns the stored copy, so a caller that has just created one knows its id.
    /// </summary>
    /// <exception cref="System.ArgumentException">The name or the SQL is blank.</exception>
    /// <exception cref="System.InvalidOperationException">Another query in the same group already
    /// has this name. Two identically named rows under one heading are indistinguishable in the
    /// picker, which is the only place these are ever chosen from.</exception>
    Task<SavedQuery> SaveAsync(SavedQuery query);

    /// <summary>Removes the query. A id that is not there is not an error — it is already gone.</summary>
    Task<bool> DeleteAsync(string id);

    /// <summary>The distinct group names in use, ordered, excluding the ungrouped empty one.</summary>
    Task<List<string>> GetGroupsAsync();

    /// <summary>
    /// Renames a group across every query in it.
    /// <para>
    /// Here rather than in the dialog because the group is denormalised onto each row: done
    /// anywhere else it is an N-row rewrite reimplemented per caller, and a half-applied one
    /// silently splits the group in two.
    /// </para>
    /// </summary>
    /// <returns>How many queries moved.</returns>
    Task<int> RenameGroupAsync(string from, string to);
}
