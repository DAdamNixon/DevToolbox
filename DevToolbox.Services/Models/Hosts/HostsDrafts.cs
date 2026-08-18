namespace DevToolbox.Services.Models.Hosts;

/// <summary>
/// An entry somebody is about to add, as typed rather than as parsed.
/// </summary>
/// <param name="Hostnames">
/// One or more names separated by whitespace, exactly as the field was filled in. Kept as text
/// because that is what the developer typed; <c>HostsLineValidator</c> owns the splitting rule so
/// there is one place that decides what counts as a name.
/// </param>
/// <param name="Comment">Optional note, written after a <c>#</c> where the hosts format allows one.</param>
public sealed record NewHostsEntry(string Address, string Hostnames, string? Comment = null);

/// <summary>An option somebody is about to add, with the entries it starts life holding.</summary>
public sealed record NewHostsOption(
    string Name,
    HostsSeverityLevel Severity = HostsSeverityLevel.Normal,
    IReadOnlyList<NewHostsEntry>? Entries = null)
{
    public IReadOnlyList<NewHostsEntry> EntryList => Entries ?? [];
}

/// <summary>A group somebody is about to add, with its options.</summary>
public sealed record NewHostsGroup(string Name, IReadOnlyList<NewHostsOption> Options);

/// <summary>
/// Something being added to the file. Closed to exactly three shapes, so the mutator, the backup
/// reason and the wording on screen are all decided by one <c>switch</c> that the compiler checks.
/// </summary>
public abstract record HostsAddition
{
    /// <summary>Private, which closes the hierarchy: only the three nested records can derive.</summary>
    private HostsAddition()
    {
    }

    /// <summary>A whole new group, appended to the end of the file.</summary>
    public sealed record Group(NewHostsGroup Value) : HostsAddition;

    /// <summary>A new option inside an existing group.</summary>
    public sealed record Option(string InGroup, NewHostsOption Value) : HostsAddition;

    /// <summary>New entries inside an existing option.</summary>
    public sealed record Entries(string InGroup, string InOption, IReadOnlyList<NewHostsEntry> Values) : HostsAddition;

    /// <summary>What the backup's file name records.</summary>
    public HostsChangeReasonKind Reason => this switch
    {
        Group => HostsChangeReasonKind.AddGroup,
        Option => HostsChangeReasonKind.AddOption,
        _ => HostsChangeReasonKind.AddEntry,
    };

    /// <summary>The group this concerns, whether it already exists or is being created.</summary>
    public string TargetGroup => this switch
    {
        Group group => group.Value.Name.Trim(),
        Option option => option.InGroup,
        Entries entries => entries.InGroup,
        _ => string.Empty,
    };

    /// <summary>The option this concerns, or null when it is a whole group.</summary>
    public string? TargetOption => this switch
    {
        Option option => option.Value.Name.Trim(),
        Entries entries => entries.InOption,
        _ => null,
    };

    /// <summary>How to describe what happened, once it has.</summary>
    public string Describe() => this switch
    {
        Group group => $"{group.Value.Name.Trim()} added with "
                       + $"{Count(group.Value.Options.Count, "option")}",
        Option option => $"{option.Value.Name.Trim()} added to {option.InGroup}",
        Entries entries => $"{Count(entries.Values.Count, "entry", "entries")} added to "
                           + $"{entries.InGroup}/{entries.InOption}",
        _ => "the file changed",
    };

    private static string Count(int n, string singular, string? plural = null) =>
        n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";
}

/// <summary>
/// One entry as the editor holds it.
/// </summary>
/// <param name="Line">
/// The line it came from, or <c>0</c> for one being added. Identity is the line number rather than
/// the content, so an entry can be edited into something completely different and still be
/// recognised as the same row instead of as a delete plus an add.
/// </param>
public sealed record HostsEntryEdit(int Line, NewHostsEntry Value);

/// <summary>
/// A change to something that already exists. Closed, like <see cref="HostsAddition"/>, so the
/// mutator, the backup reason and the wording all come from one compiler-checked <c>switch</c>.
/// </summary>
public abstract record HostsEdit
{
    private HostsEdit()
    {
    }

    public sealed record RenameGroup(string InGroup, string NewName) : HostsEdit;

    public sealed record DeleteGroup(string InGroup) : HostsEdit;

    /// <summary>
    /// The option's name, flag and entries as they should end up.
    /// </summary>
    /// <param name="Entries">
    /// The complete list the option should hold. Rows carrying a line number are kept and rewritten
    /// where they differ, rows without one are added, and any line the option owns that is
    /// <em>absent</em> from this list is deleted. Stating the destination rather than a list of
    /// operations is what lets one dialog produce one change set, one preview and one write.
    /// </param>
    public sealed record UpdateOption(
        string InGroup,
        string InOption,
        string NewName,
        HostsSeverityLevel Severity,
        IReadOnlyList<HostsEntryEdit> Entries) : HostsEdit;

    public sealed record DeleteOption(string InGroup, string InOption) : HostsEdit;

    /// <summary>What the backup's file name records.</summary>
    public HostsChangeReasonKind Reason => this switch
    {
        DeleteGroup or DeleteOption => HostsChangeReasonKind.Delete,
        _ => HostsChangeReasonKind.Edit,
    };

    public string TargetGroup => this switch
    {
        RenameGroup rename => rename.InGroup,
        DeleteGroup delete => delete.InGroup,
        UpdateOption update => update.InGroup,
        DeleteOption delete => delete.InGroup,
        _ => string.Empty,
    };

    public string? TargetOption => this switch
    {
        UpdateOption update => update.InOption,
        DeleteOption delete => delete.InOption,
        _ => null,
    };

    /// <summary>How to describe what happened, once it has.</summary>
    public string Describe() => this switch
    {
        RenameGroup rename => $"{rename.InGroup} renamed to {rename.NewName.Trim()}",
        DeleteGroup delete => $"{delete.InGroup} deleted",
        UpdateOption update when !string.Equals(update.NewName.Trim(), update.InOption, StringComparison.Ordinal) =>
            $"{update.InGroup}/{update.InOption} renamed to {update.NewName.Trim()} and updated",
        UpdateOption update => $"{update.InGroup}/{update.InOption} updated",
        DeleteOption delete => $"{delete.InOption} deleted from {delete.InGroup}",
        _ => "the file changed",
    };
}
