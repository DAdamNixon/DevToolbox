using System.Net;
using System.Net.Sockets;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>What kind of thing a group's entries point at.</summary>
public enum HostsGroupShape
{
    /// <summary>No entry in the group carries a usable address.</summary>
    Unknown,

    /// <summary>Everything comes back to this machine.</summary>
    Loopback,

    /// <summary>
    /// This machine is one of the places the group can point, alongside others — the shape of a
    /// "whose copy am I using" group, where one of the choices is your own.
    /// </summary>
    LocalOrRemote,

    /// <summary>Everything is on one network — one destination, however many names it has.</summary>
    SingleNetwork,

    /// <summary>Entries are spread across networks, so the group is a genuine choice of destination.</summary>
    SeveralNetworks,
}

/// <summary>
/// Classifies a group by where it sends traffic.
/// <para>
/// It exists so that presentation has something to key off that is not the group's <em>name</em>.
/// Naming is a team's vocabulary and has no business reaching the code; where the packets go is a
/// fact about the file and is true of anybody's. This is the same reasoning that keeps the severity
/// flag words in config, applied to a smaller decision.
/// </para>
/// </summary>
public static class HostsGroupShapes
{
    public static HostsGroupShape Of(HostsGroup group, HostsMap map)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(map);

        var addresses = map.Entries
            // Quarantined lines are excluded here for the same reason they are excluded from an
            // option's counts and from a switch: the analyzer's whole finding is that they are not
            // the group's, so letting them decide how the group is described would undo that.
            .Where(entry => string.Equals(entry.Group, group.Name, StringComparison.Ordinal) && !entry.IsSuspect)
            .Select(entry => IPAddress.TryParse(entry.Address, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToArray();

        if (addresses.Length == 0) return HostsGroupShape.Unknown;

        // Loopback is tested for presence rather than for being everything. A group offering "my
        // machine" alongside two colleagues' is still a "runs here" group, and describing it by the
        // spread of the other options buries the one fact about it that a developer cares about.
        if (addresses.Any(IPAddress.IsLoopback))
        {
            return addresses.All(IPAddress.IsLoopback) ? HostsGroupShape.Loopback : HostsGroupShape.LocalOrRemote;
        }

        var networks = addresses.Select(NetworkOf).Distinct(StringComparer.Ordinal).Count();

        return networks == 1 ? HostsGroupShape.SingleNetwork : HostsGroupShape.SeveralNetworks;
    }

    /// <summary>
    /// The address's network as a comparable string — /24 for IPv4, /64 for IPv6. Coarse on
    /// purpose: this only decides how a group is described, so neighbouring hosts should count as
    /// the same place rather than as a spread.
    /// </summary>
    private static string NetworkOf(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var significant = address.AddressFamily == AddressFamily.InterNetworkV6 ? 8 : 3;

        return Convert.ToHexString(bytes, 0, Math.Min(significant, bytes.Length));
    }
}
