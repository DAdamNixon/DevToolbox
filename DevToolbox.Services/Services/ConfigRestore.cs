using System.Security.Cryptography;

namespace DevToolbox.Services.Services;

/// <summary>
/// The other half of <see cref="ConfigDefaults"/>: putting a shipped configuration file back.
/// <para>
/// Seeding deliberately never overwrites, which is the right default — an installer runs again on
/// every upgrade, and these files are hand-edited and comment-annotated. But that left no way at all
/// to say "give me the shipped one back", which is what someone wants after breaking a config they
/// were experimenting with. This is that, and it is a deliberate action with a named file rather
/// than a mode the installer runs in.
/// </para>
/// <para>
/// Restoring always writes a dated backup of what it replaces first. The rule is the same one the
/// rest of the application follows: a file the user has is never destroyed without a copy, because
/// serialization keeps neither comments nor key order and an overwritten annotated config cannot be
/// reconstructed.
/// </para>
/// </summary>
public static class ConfigRestore
{
    public enum State
    {
        /// <summary>The machine does not have this file. Restoring is a plain copy.</summary>
        Missing,

        /// <summary>Byte-identical to the shipped copy. Restoring would change nothing.</summary>
        Unchanged,

        /// <summary>Edited, or shipped in a newer form. Restoring replaces it, keeping a backup.</summary>
        Modified,
    }

    /// <param name="Name">File name, e.g. <c>log_paths.yaml</c>.</param>
    public sealed record Comparison(string Name, State State);

    /// <summary>
    /// Every shipped file, and how the live copy compares. Empty when nothing was bundled — a plain
    /// build or a clone has no defaults to restore from, and the UI should say so rather than offer
    /// buttons that do nothing.
    /// </summary>
    public static IReadOnlyList<Comparison> Compare(string? sourceDirectory, string? configDirectory)
    {
        var results = new List<Comparison>();

        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(configDirectory)) return results;

        try
        {
            if (!Directory.Exists(sourceDirectory)) return results;

            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*.yaml"))
            {
                var name = Path.GetFileName(source);
                var live = Path.Combine(configDirectory, name);

                results.Add(new Comparison(name, !File.Exists(live)
                    ? State.Missing
                    : SameContent(source, live) ? State.Unchanged : State.Modified));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was compared before the failure is still worth showing.
        }

        return results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Replaces one live file with the shipped one, backing up what was there.
    /// </summary>
    /// <param name="timestamp">
    /// Stamped into the backup name. Passed in rather than read from the clock so the naming is
    /// testable, and so restoring several files in one action groups them under one moment.
    /// </param>
    /// <returns>The backup written, or null if there was nothing to back up or the restore failed.</returns>
    public static string? Restore(string name, string? sourceDirectory, string? configDirectory, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(configDirectory))
        {
            return null;
        }

        // Only ever a file name. A name carrying a path separator would let a caller write outside
        // the config folder entirely, and nothing legitimate needs it.
        if (name != Path.GetFileName(name)) return null;

        var source = Path.Combine(sourceDirectory, name);
        var live = Path.Combine(configDirectory, name);

        try
        {
            if (!File.Exists(source)) return null;

            string? backup = null;
            if (File.Exists(live))
            {
                // Seconds, not just the date: restore, edit, restore again on the same day is an
                // ordinary thing to do while experimenting, and a name that collides would throw
                // away the very copy the backup exists to keep.
                backup = $"{live}.bak-{timestamp:yyyy-MM-dd-HHmmss}";
                File.Copy(live, backup, overwrite: true);
            }

            Directory.CreateDirectory(configDirectory);
            File.Copy(source, live, overwrite: true);

            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameContent(string left, string right)
    {
        try
        {
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);

            // Hashed rather than compared byte by byte because these are small and it keeps the
            // comparison to one pass each with no buffer juggling.
            return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as identical; offering a restore is the safer answer.
            return false;
        }
    }
}
