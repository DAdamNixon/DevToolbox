namespace DevToolbox.Services.Models.Hosts;

/// <summary>What a mutation did to one line.</summary>
public enum HostsLineChangeKind
{
    /// <summary>An enabled line was commented out.</summary>
    Commented,

    /// <summary>A commented line was enabled.</summary>
    Uncommented,

    /// <summary>A line that did not exist before was added.</summary>
    Inserted,

    /// <summary>A line's content was rewritten — a rename, a re-flagged option, a corrected entry.</summary>
    Edited,

    /// <summary>A line was removed.</summary>
    Deleted,
}

/// <summary>
/// One line's before and after. The unit the confirmation dialog renders as a diff and the
/// unit <c>HostsInvariantChecker</c> validates, so what a developer approves and what gets
/// verified are literally the same list.
/// </summary>
/// <param name="Line">
/// Where this sits in the original document. For an insertion, the line it goes <em>before</em> —
/// one past the last line means appended. Together with <see cref="ResultLine"/> this makes the
/// change list a complete edit script, which is what lets the checker rebuild the result and
/// compare it rather than spot-checking it.
/// </param>
public sealed record HostsLineChange(int Line, string Before, string After, HostsLineChangeKind Kind)
{
    /// <summary>Where this ends up in the resulting document. Zero for a deletion.</summary>
    public int ResultLine { get; init; }

    /// <summary>The line number worth showing beside this change in a diff.</summary>
    public int DisplayLine => Kind == HostsLineChangeKind.Deleted ? Line : ResultLine == 0 ? Line : ResultLine;

    /// <summary>
    /// Whether this is one of the two kinds a switch produces. Those carry a much stronger promise
    /// than an edit does — see <c>HostsInvariantChecker</c> — so they are recognised in one place.
    /// </summary>
    public bool IsMarkerOnly => Kind is HostsLineChangeKind.Commented or HostsLineChangeKind.Uncommented;
}

/// <summary>The result of a pure mutation: a new document plus exactly what changed to get there.</summary>
public sealed record HostsMutation(HostsDocument Document, IReadOnlyList<HostsLineChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>
/// A dry run: what a switch would do, and what stands in its way. Produced without touching
/// the file, so the diff a developer sees is the diff that gets applied.
/// </summary>
/// <param name="Option">The option being switched to, or <c>null</c> to turn the group off.</param>
/// <param name="Blocking">
/// Anomalies affecting the lines this change would touch. Non-empty means the change needs
/// explicit confirmation, not that it is impossible.
/// </param>
/// <param name="SuspectLinesIncluded">Whether quarantined lines were deliberately swept in.</param>
public sealed record HostsChangePreview(
    HostsChangeReasonKind Reason,
    string? Group,
    string? Option,
    HostsDocument Result,
    IReadOnlyList<HostsLineChange> Changes,
    IReadOnlyList<HostsAnomaly> Blocking,
    bool SuspectLinesIncluded)
{
    /// <summary>Nothing to do — the group is already in the requested state.</summary>
    public bool IsNoOp => Changes.Count == 0;

    public bool IsBlocked => Blocking.Count > 0;

    public int CommentedCount => Changes.Count(c => c.Kind == HostsLineChangeKind.Commented);

    public int UncommentedCount => Changes.Count(c => c.Kind == HostsLineChangeKind.Uncommented);

    /// <summary>
    /// Whether this rewrites or removes existing content, rather than only moving comment markers
    /// or adding lines. The dialog leans on it: a change that can lose something a developer typed
    /// deserves more than the wording used for switching an option on.
    /// </summary>
    public bool IsDestructive => Changes.Any(change =>
        change.Kind is HostsLineChangeKind.Edited or HostsLineChangeKind.Deleted);
}
