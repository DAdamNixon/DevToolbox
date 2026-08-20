using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Reads an existing option back out as a draft, so it can be added again under a new name.
/// <para>
/// This is the "same names, different address" case, which is most of what a hosts file is for: the
/// interesting difference between two options in a group is almost always the address, and typing
/// out twenty hostnames a second time is how a typo gets in. Copying keeps the names by
/// construction and leaves exactly one thing to change.
/// </para>
/// <para>
/// The copy is composed from the parsed entries rather than from the raw text, so it is normalised
/// on the way through: a line carrying its note in brackets after the hostnames comes out with the
/// note behind a marker, where the hosts format actually allows one.
/// </para>
/// </summary>
public static class HostsOptionCopy
{
    /// <summary>
    /// The option's own entries as a draft.
    /// </summary>
    /// <param name="address">
    /// Given to every entry, replacing whatever each one had. Null keeps each entry's own address,
    /// which is the plain duplicate.
    /// </param>
    /// <param name="severity">Null keeps the source's flag.</param>
    public static NewHostsOption From(
        HostsDocument document,
        HostsOption source,
        string newName,
        string? address = null,
        HostsSeverityLevel? severity = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);

        var entries = EntriesOf(document, source);

        if (!string.IsNullOrWhiteSpace(address))
        {
            var replacement = address.Trim();
            entries = entries.Select(entry => entry with { Address = replacement }).ToArray();
        }

        return new NewHostsOption(newName, severity ?? source.Severity, entries);
    }

    /// <summary>
    /// The entries an option owns, in file order.
    /// <para>
    /// Only the lines it unambiguously owns. A line tagged individually belongs to its own directive
    /// and copying it would silently drop that; a parked line is one somebody deliberately keeps out
    /// of use; and a quarantined line is not the option's at all.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NewHostsEntry> EntriesOf(HostsDocument document, HostsOption source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);

        return source.OwnedLines
            .Select(line => HostsLineValidator.DecomposeEntry(document.Lines[line - 1].Text))
            .OfType<NewHostsEntry>()
            .ToArray();
    }

    /// <summary>
    /// The one address every entry points at, or <c>null</c> when they do not agree.
    /// <para>
    /// Used to fill in the "change the address to" box, so the box shows what is being replaced
    /// rather than starting empty. An option whose entries genuinely differ has no such answer, and
    /// saying nothing is better than picking one of them and implying the rest match.
    /// </para>
    /// </summary>
    public static string? SharedAddress(HostsDocument document, HostsOption source)
    {
        var addresses = EntriesOf(document, source)
            .Select(entry => entry.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        return addresses.Length == 1 ? addresses[0] : null;
    }
}
