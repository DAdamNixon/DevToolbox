using System.Security.Cryptography;
using System.Text;

namespace DevToolbox.Services;

/// <summary>
/// The names the two halves of the single-instance guard agree on: the mutex that decides which
/// process wins, and the registered window message the loser uses to wake the winner.
/// <para>
/// Here, in Services, rather than beside the Win32 plumbing that uses it, because everything that
/// can go wrong is in the naming and none of it is in the P/Invoke — and a mutex name is only
/// testable if deriving it is separable from acquiring it.
/// </para>
/// </summary>
public static class SingleInstanceKey
{
    /// <summary>
    /// One instance per copy of the application, not one per machine.
    /// <para>
    /// Deliberate: a single fixed name would mean pressing F5 in Visual Studio while the installed
    /// package is running silently foregrounds the installed copy instead of starting the build you
    /// just made. That is annoying enough that the guard would get deleted, so the installed package
    /// and a working copy are allowed to coexist. Two clicks of the same shortcut — the case that
    /// was actually reported — still resolve to the same name.
    /// </para>
    /// <para>
    /// An MSIX upgrade installs into a version-stamped folder, so this name changes with the
    /// version. Harmless: an upgrade replaces the running copy rather than joining it.
    /// </para>
    /// </summary>
    /// <param name="executablePath">
    /// Normally <see cref="Environment.ProcessPath"/>. Null or blank yields a fixed fallback name,
    /// which keeps the guard working rather than turning an unknowable path into no guard at all.
    /// </param>
    public static string MutexName(string? executablePath) =>
        // Local\ is the default, and is stated anyway to record that it is meant: the Global\
        // namespace would make one instance per *machine*, so a second person signed in over RDP
        // could not open DevToolbox at all. Configuration is per-user, so instances should be too.
        $@"Local\DevToolbox.SingleInstance.{Fingerprint(executablePath)}";

    /// <summary>
    /// The string handed to <c>RegisterWindowMessage</c>, so a second instance can ask the first to
    /// show itself. Carries the same fingerprint as the mutex, so a broadcast cannot reach a copy
    /// that was allowed to be running separately.
    /// </summary>
    public static string ShowWindowMessageName(string? executablePath) =>
        $"DevToolbox.ShowExistingWindow.{Fingerprint(executablePath)}";

    /// <summary>
    /// A stable short hash of the path.
    /// <para>
    /// Hashed rather than embedded for two reasons: a backslash separates namespaces in a kernel
    /// object name, so a path cannot appear in one literally; and the name has a length limit a
    /// deep path would exceed.
    /// </para>
    /// <para>
    /// SHA-256 rather than <see cref="string.GetHashCode()"/>, which is seeded per process — two
    /// instances would compute different names from the same path and neither would ever see the
    /// other. That is the whole bug this guard exists to fix, reintroduced one layer down.
    /// </para>
    /// </summary>
    private static string Fingerprint(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "unknown";

        // Windows paths are case-insensitive and a trailing separator is not a difference, so
        // neither should change the identity of the instance.
        var normalized = executablePath.Trim().TrimEnd('\\', '/').ToLowerInvariant().Replace('/', '\\');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
