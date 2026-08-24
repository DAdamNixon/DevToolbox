using System.Collections.Generic;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// Read/write access to the Log Viewer's own configuration: the template index, the template files
/// it points at, and the list of locations.
/// <para>
/// Separate from <see cref="ILogFileService"/>, which reads the same files but only ever reads
/// them. Keeping the writes here means the query engine stays a query engine, and the rules that
/// have to hold across <em>two</em> files — a template's index entry and its body — live in one
/// place instead of being reimplemented by whatever dialog is saving.
/// </para>
/// </summary>
public interface ILogConfigService
{
    /// <summary>The index: what each template is called, and which file holds it.</summary>
    Task<List<LogTemplateIndexEntry>> GetTemplatesAsync();

    /// <param name="file">The index entry's <c>File</c>, with or without its <c>.yaml</c>.</param>
    Task<LogTemplate?> LoadTemplateAsync(string file);

    /// <summary>
    /// Writes the template body and brings its index entry into line with it, adding the entry when
    /// there isn't one yet.
    /// </summary>
    /// <param name="existingFile">The file to overwrite, or null/empty to create a new template —
    /// in which case a file name is derived from <paramref name="template"/>'s name.</param>
    /// <returns>The index entry as it now stands, so a caller that just created a template knows
    /// which file it landed in.</returns>
    Task<LogTemplateIndexEntry> SaveTemplateAsync(string? existingFile, LogTemplate template);

    /// <summary>
    /// Drops the template's index entry and its file.
    /// <para>
    /// The entry goes first: an orphaned file is invisible, whereas an index entry pointing at a
    /// file that is gone is a template that appears in the picker and then fails to load.
    /// </para>
    /// </summary>
    Task DeleteTemplateAsync(string file);

    /// <summary>
    /// Which templates name <paramref name="file"/> as their <c>inherits</c> base, by template name.
    /// Deleting a base leaves those templates short of its columns, so the caller can say so first.
    /// </summary>
    Task<List<string>> FindTemplatesInheritingAsync(string file);

    Task<List<LogLocation>> GetLocationsAsync();

    /// <summary>Replaces the whole list — the file holds nothing else.</summary>
    Task SaveLocationsAsync(IReadOnlyList<LogLocation> locations);
}
