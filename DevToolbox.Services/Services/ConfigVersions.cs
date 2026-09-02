using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevToolbox.Services.Services;

/// <summary>
/// What a config file can be compared against and put back from: the copy shipped with the install,
/// and every backup already sitting beside it.
/// <para>
/// Sits beside <see cref="ConfigRestore"/> rather than inside it. That class does one thing — put
/// the shipped file back — and is covered by tests written against exactly that contract; this adds
/// the *choosing* of a version, which the UI needs and the restore itself does not.
/// </para>
/// <para>
/// The backups are not new. Two different pieces of code have been writing them all along, in two
/// different shapes, and anything that lists them has to understand both:
/// <list type="bullet">
///   <item><c>&lt;file&gt;.yaml.bak</c> — written by <c>YamlStorageService</c> before every save.
///   There is only ever one, overwritten each time, and its name carries no date, so its age has to
///   come from the file's own timestamp.</item>
///   <item><c>&lt;file&gt;.yaml.bak-yyyy-MM-dd-HHmmss</c> — written by <see cref="ConfigRestore"/>
///   before every restore. These accumulate, and the date is in the name.</item>
/// </list>
/// </para>
/// </summary>
public static class ConfigVersions
{
    /// <summary>The suffix <c>YamlStorageService</c> uses for its single pre-save copy.</summary>
    private const string PlainBackupSuffix = ".bak";

    /// <summary>The prefix <see cref="ConfigRestore"/>'s dated backups carry after the file name.</summary>
    private const string DatedBackupPrefix = ".bak-";

    private const string DatedBackupFormat = "yyyy-MM-dd-HHmmss";

    public enum Origin
    {
        /// <summary>The copy bundled with the install.</summary>
        Shipped,

        /// <summary>A copy of the live file kept before something overwrote it.</summary>
        Backup,
    }

    /// <param name="Label">What to call this in the picker, e.g. "Shipped with this install".</param>
    /// <param name="Taken">When the version dates from, or null when nothing can say.</param>
    public sealed record Version(Origin Origin, string Label, string FileName, string FullPath, DateTime? Taken);

    /// <summary>
    /// Every version of <paramref name="name"/> worth comparing against: the shipped copy first when
    /// there is one, then the backups newest first.
    /// <para>
    /// An empty list is a real answer, not a failure — a build run from source has no bundled
    /// configuration, and a file nobody has ever overwritten has no backups.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Version> For(string name, string? sourceDirectory, string? configDirectory)
    {
        var versions = new List<Version>();
        if (!IsPlainFileName(name)) return versions;

        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var shipped = Path.Combine(sourceDirectory, name);
            if (SafeExists(shipped))
                versions.Add(new Version(Origin.Shipped, "Shipped with this install", name, shipped, null));
        }

        if (string.IsNullOrWhiteSpace(configDirectory)) return versions;

        var backups = new List<Version>();

        try
        {
            if (!Directory.Exists(configDirectory)) return versions;

            // Both shapes at once: "<name>.bak" and "<name>.bak-<stamp>".
            foreach (var path in Directory.EnumerateFiles(configDirectory, name + PlainBackupSuffix + "*"))
            {
                var fileName = Path.GetFileName(path);
                var suffix = fileName[name.Length..];

                DateTime? taken;
                string label;

                if (string.Equals(suffix, PlainBackupSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // No date in the name, so the file's own is the only evidence there is.
                    taken = SafeLastWrite(path);
                    label = "Before the last save in DevToolbox";
                }
                else if (suffix.StartsWith(DatedBackupPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    taken = ParseStamp(suffix[DatedBackupPrefix.Length..]) ?? SafeLastWrite(path);
                    label = "Replaced by a restore";
                }
                else
                {
                    // Something else entirely that happens to start with .bak — not ours to offer.
                    continue;
                }

                backups.Add(new Version(Origin.Backup, label, fileName, path, taken));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was found before the failure is still worth offering.
        }

        // Newest first. A null date sorts last: it is the one nothing could be established about,
        // so it is the one least likely to be what someone is reaching for.
        versions.AddRange(backups
            .OrderByDescending(b => b.Taken ?? DateTime.MinValue)
            .ThenBy(b => b.FileName, StringComparer.OrdinalIgnoreCase));

        return versions;
    }

    /// <summary>The file's content, or null when it cannot be read.</summary>
    public static string? ReadText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="version"/> over the live copy of <paramref name="name"/>, keeping a
    /// dated backup of what was there.
    /// <para>
    /// Restoring *from* a backup still earns a backup. It is no less a destructive write to the live
    /// file than restoring from shipped is, and someone comparing two backups to decide between them
    /// must not lose the third thing — what they had before they started — by picking wrong.
    /// </para>
    /// </summary>
    /// <returns>The backup written, or null if there was nothing to back up or the restore failed.</returns>
    public static string? RestoreFrom(Version? version, string name, string? configDirectory, DateTime timestamp)
    {
        if (version is null || string.IsNullOrWhiteSpace(configDirectory)) return null;
        if (!IsPlainFileName(name)) return null;

        // The version's path is produced by For() and never by a caller, but a restore writes over a
        // config file — so it is checked here too rather than trusted to have come from there.
        if (!IsPlainFileName(version.FileName)) return null;

        var live = Path.Combine(configDirectory, name);

        try
        {
            if (!File.Exists(version.FullPath)) return null;

            // Restoring a file onto itself would truncate it: File.Copy with overwrite opens the
            // destination for writing before reading the source.
            if (PathsMatch(version.FullPath, live)) return null;

            string? backup = null;
            if (File.Exists(live))
            {
                backup = $"{live}{DatedBackupPrefix}{timestamp.ToString(DatedBackupFormat)}";
                File.Copy(live, backup, overwrite: true);
            }

            Directory.CreateDirectory(configDirectory);
            File.Copy(version.FullPath, live, overwrite: true);

            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A bare file name — no directory part, no traversal. Anything else could write outside the
    /// config folder, and nothing legitimate here needs it.
    /// </summary>
    private static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name);

    private static DateTime? ParseStamp(string stamp) =>
        DateTime.TryParseExact(stamp, DatedBackupFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime? SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
