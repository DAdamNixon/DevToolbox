using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.UI.Models;

/// <summary>
/// The mutable half-finished versions of the authoring models, for two-way binding.
/// <para>
/// The models in <c>DevToolbox.Services</c> are records because a request to change a file should
/// not be able to change under the code handling it. A form is the opposite: it exists precisely to
/// be edited a keystroke at a time, and half of what it holds is invalid most of the time it is on
/// screen. So the two are kept apart, and <c>ToModel</c> is the one crossing point.
/// </para>
/// </summary>
public sealed class HostsEntryForm
{
    /// <summary>
    /// The line this row came from, or <c>0</c> for one being added. Carried so the editor can hand
    /// back an entry's identity: a row edited into something completely different is still the same
    /// line, not a delete plus an add.
    /// </summary>
    public int Line { get; init; }

    public string Address { get; set; } = string.Empty;

    public string Hostnames { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    /// <summary>Nothing typed yet, so it should not be complained about.</summary>
    public bool IsBlank =>
        Address.Trim().Length == 0 && Hostnames.Trim().Length == 0 && Comment.Trim().Length == 0;

    public NewHostsEntry ToModel() =>
        new(Address.Trim(), Hostnames.Trim(), Comment.Trim().Length == 0 ? null : Comment.Trim());

    public HostsEntryEdit ToEdit() => new(Line, ToModel());

    /// <summary>A row prefilled from a line already in the file.</summary>
    public static HostsEntryForm From(int line, NewHostsEntry entry) => new()
    {
        Line = line,
        Address = entry.Address,
        Hostnames = entry.Hostnames,
        Comment = entry.Comment ?? string.Empty,
    };
}

/// <inheritdoc cref="HostsEntryForm"/>
public sealed class HostsOptionForm
{
    public string Name { get; set; } = string.Empty;

    public HostsSeverityLevel Severity { get; set; } = HostsSeverityLevel.Normal;

    public List<HostsEntryForm> Entries { get; } = [new()];

    /// <summary>Rows the developer actually filled in. A trailing empty row is how you add another.</summary>
    public IReadOnlyList<HostsEntryForm> FilledEntries => Entries.Where(entry => !entry.IsBlank).ToArray();

    public NewHostsOption ToModel() =>
        new(Name.Trim(), Severity, FilledEntries.Select(entry => entry.ToModel()).ToArray());
}
