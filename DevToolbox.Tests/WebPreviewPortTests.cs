using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// Port selection for the browser view.
/// <para>
/// This exists because of a crash at launch: with 5218 already taken — a second instance, or a dev
/// server left running — port selection fell back to 0, and Kestrel rejects <c>ListenLocalhost(0)</c>
/// outright ("Dynamic port binding is not supported when binding to localhost"). It throws that from
/// <c>WebApplicationBuilder.Build</c>, which is <em>before</em> the try/catch in StartAsync that is
/// supposed to make a bad port cost only the browser view. So the whole WinForms app died on a busy
/// port.
/// </para>
/// <para>
/// Reached by reflection rather than by making the methods public: the fix is about what the host
/// does at startup, and widening its surface to test it would be the wrong trade.
/// </para>
/// <para>
/// No port number here is hardcoded. Windows reserves ranges for Hyper-V and WinNAT in which a bind
/// fails with WSAEACCES whether or not anything is listening, so a fixed "surely nothing uses this"
/// port makes the test pass or fail depending on the machine. Every test asks
/// <see cref="Reserve"/> for a block that demonstrably binds.
/// </para>
/// </summary>
public class WebPreviewPortTests
{
    private static readonly Type Host = typeof(DevToolbox.UI.Web.WebPreviewHost);

    private static int ChoosePort(int preferred) =>
        (int)Host.GetMethod("ChoosePort", BindingFlags.NonPublic | BindingFlags.Static)!
                 .Invoke(null, new object[] { preferred })!;

    private static bool IsFree(int port) =>
        (bool)Host.GetMethod("IsFree", BindingFlags.NonPublic | BindingFlags.Static)!
                  .Invoke(null, new object[] { port })!;

    /// <summary>
    /// Holds a run of consecutive ports on both loopback stacks, the way running instances do. Both
    /// stacks, because that is what <c>ListenLocalhost</c> binds and therefore what has to be probed.
    /// </summary>
    private sealed class PortHog : IDisposable
    {
        private readonly List<TcpListener> _held;

        public int BasePort { get; }

        private PortHog(int basePort, List<TcpListener> held)
        {
            BasePort = basePort;
            _held = held;
        }

        /// <summary>Takes <paramref name="count"/> ports from <paramref name="basePort"/>, or null if any refuses.</summary>
        public static PortHog? TryTake(int basePort, int count)
        {
            var held = new List<TcpListener>();
            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                    {
                        var listener = new TcpListener(address, basePort + offset);
                        listener.Start();
                        held.Add(listener);
                    }
                }

                return new PortHog(basePort, held);
            }
            catch (SocketException)
            {
                foreach (var listener in held) listener.Stop();
                return null;
            }
        }

        /// <summary>Lets go of one port in the run, so a test can leave a deliberate gap.</summary>
        public void Release(int port)
        {
            for (var i = _held.Count - 1; i >= 0; i--)
            {
                if (((IPEndPoint)_held[i].LocalEndpoint).Port != port) continue;
                _held[i].Stop();
                _held.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            foreach (var listener in _held) listener.Stop();
            _held.Clear();
        }
    }

    /// <summary>
    /// A run of <paramref name="count"/> consecutive ports that this machine will actually bind.
    /// Stepped by more than the run length so a partial failure does not overlap the next attempt.
    /// </summary>
    private static PortHog Reserve(int count)
    {
        for (var basePort = 52180; basePort < 60000; basePort += count + 1)
        {
            if (PortHog.TryTake(basePort, count) is { } hog) return hog;
        }

        throw new InvalidOperationException($"no run of {count} bindable loopback ports on this machine");
    }

    [Fact]
    public void A_free_port_is_taken_as_it_stands()
    {
        int port;
        using (var hog = Reserve(1)) port = hog.BasePort;

        // Just released, so it is free and known to bind on both stacks.
        Assert.Equal(port, ChoosePort(port));
    }

    [Fact]
    public void A_busy_port_moves_to_the_next_one_rather_than_to_zero()
    {
        using var hog = Reserve(1);

        var chosen = ChoosePort(hog.BasePort);

        // Zero is what caused the crash. The contract is a concrete port.
        Assert.NotEqual(0, chosen);
        Assert.True(chosen > hog.BasePort, $"expected a port after {hog.BasePort}, got {chosen}");
        Assert.True(IsFree(chosen), "the chosen port has to be one that will actually bind");
    }

    [Fact]
    public void A_port_held_on_one_stack_only_still_counts_as_busy()
    {
        // ListenLocalhost binds both stacks and fails if either refuses, so probing IPv4 alone would
        // hand back a port that then dies at bind time — inside StartAsync, costing the browser view.
        int port;
        using (var probe = Reserve(1)) port = probe.BasePort;

        var v6Only = new TcpListener(IPAddress.IPv6Loopback, port);
        v6Only.Start();
        try
        {
            Assert.False(IsFree(port));
        }
        finally
        {
            v6Only.Stop();
        }
    }

    [Fact]
    public void A_run_of_busy_ports_is_walked_past()
    {
        // Four reserved, the last released: the gap is a port known to bind, so the assertion is
        // about the walk and not about what else the machine happens to be running.
        using var hog = Reserve(4);
        var gap = hog.BasePort + 3;
        hog.Release(gap);

        Assert.Equal(gap, ChoosePort(hog.BasePort));
    }

    [Fact]
    public void Ten_ports_are_scanned_before_giving_up_and_asking_the_OS()
    {
        // The fallback still has to exist — it is what makes the bind unable to throw — but it must
        // take a genuinely blocked run to reach it, not a single clash.
        using var hog = Reserve(10);

        Assert.Equal(0, ChoosePort(hog.BasePort));
    }
}
