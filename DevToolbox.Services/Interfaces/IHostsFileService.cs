using DevToolbox.Services.Models;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// The one place that reads, watches and changes the hosts file.
/// <para>
/// Registered as a singleton because both the Host Changer tab and the tray icon have to agree
/// about what is switched on, and because the watcher and poll loop must outlive any one page. The
/// tab subscribes to <see cref="Changed"/> exactly as the Service Pulse page subscribes to health
/// changes, rather than polling.
/// </para>
/// </summary>
public interface IHostsFileService : IDisposable
{
    /// <summary>The file being operated on, from settings or the system default.</summary>
    string HostsPath { get; }

    /// <summary>The most recent read, or null before the first one has finished.</summary>
    HostsSnapshot? Current { get; }

    /// <summary>Why the file could not be read, or null.</summary>
    string? LoadError { get; }

    /// <summary>A change is in flight. While true the UI and the tray must not start another.</summary>
    bool IsApplying { get; }

    /// <summary>
    /// Raised whenever the file's content changes, including when somebody else edits it.
    /// <para>
    /// Fires on a background thread — Blazor subscribers must marshal with <c>InvokeAsync</c>.
    /// </para>
    /// </summary>
    event EventHandler<HostsSnapshotChangedEventArgs>? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<HostsSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What switching <paramref name="group"/> to <paramref name="option"/> would do, without doing
    /// it. This is what the confirmation dialog renders, and it is the same change set that gets
    /// applied and validated.
    /// </summary>
    /// <param name="option">Null turns the group off.</param>
    HostsChangePreview Preview(HostsSnapshot snapshot, string group, string? option, bool includeSuspectLines = false);

    Task<HostsApplyResult> ApplyAsync(
        string group,
        string? option,
        HostsApplyOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a scope-closing directive before <paramref name="beforeLine"/>.</summary>
    Task<HostsApplyResult> InsertClearAsync(int beforeLine, CancellationToken cancellationToken = default);

    /// <summary>
    /// The lines an addition would put in the file, without putting them there. Rendered live in the
    /// form that is composing it, so what is on screen is produced by the same code that writes.
    /// </summary>
    /// <exception cref="ArgumentException">The addition is not valid.</exception>
    /// <exception cref="KeyNotFoundException">It names a group or option that does not exist.</exception>
    /// <exception cref="InvalidOperationException">The name is taken, or the option has no block to add to.</exception>
    HostsChangePreview PreviewAddition(HostsSnapshot snapshot, HostsAddition addition);

    /// <summary>
    /// Adds a group, an option, or entries. Additive only — nothing here renames, reorders or
    /// removes, which is what lets it share the switch's proof that no existing line was touched.
    /// </summary>
    Task<HostsApplyResult> AddAsync(HostsAddition addition, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="PreviewAddition"/>
    HostsChangePreview PreviewEdit(HostsSnapshot snapshot, HostsEdit edit);

    /// <summary>
    /// Renames, rewrites or removes something that already exists.
    /// <para>
    /// Unlike a switch or an addition, this can lose content a developer typed — so it always shows
    /// its diff first, and the diff is guaranteed complete rather than merely indicative.
    /// </para>
    /// </summary>
    Task<HostsApplyResult> EditAsync(HostsEdit edit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves edited text from the raw editor.
    /// </summary>
    /// <param name="expectedSha256">
    /// The hash the editor loaded. Stops a save built on a stale view of the file.
    /// </param>
    Task<HostsApplyResult> SaveRawAsync(
        string text,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    Task<HostsApplyResult> RestoreBackupAsync(string backupId, CancellationToken cancellationToken = default);

    Task<OpenResult> OpenHostsFileAsync();

    Task<OpenResult> OpenHostsFolderAsync();
}
