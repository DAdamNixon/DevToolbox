namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Where the Host Changer keeps its working files, all under the same AppData folder every other
/// DevToolbox config lives in.
/// <para>
/// Shared between the ordinary run and the elevated one, because the elevated side validates that
/// a staged payload really came from here before copying it over a system file.
/// </para>
/// </summary>
public static class HostsPaths
{
    private const string AppFolderName = "DevToolbox";

    /// <summary>Staged payloads waiting to be written. One directory per request, deleted after.</summary>
    private const string RequestFolderName = "HostsWrite";

    /// <summary>Copies of the hosts file taken before each change.</summary>
    private const string BackupFolderName = "HostsBackups";

    public const string PayloadFileName = "payload.hosts";
    public const string RequestFileName = "request.json";
    public const string ResultFileName = "result.json";

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string RequestRoot => Path.Combine(AppDataRoot, RequestFolderName);

    public static string BackupRoot => Path.Combine(AppDataRoot, BackupFolderName);

    /// <summary>Creates and returns a fresh request directory.</summary>
    public static string CreateRequestDirectory(Guid id)
    {
        var directory = Path.Combine(RequestRoot, id.ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> really sits inside the request root.
    /// <para>
    /// The elevated run will copy a file from here over a system file, so without this check the
    /// application would be a general-purpose "copy anything anywhere as administrator" tool for
    /// anyone who could pass it a path. Compared after <see cref="Path.GetFullPath"/> so
    /// <c>..</c> cannot walk out, and symlinks are rejected so the check cannot be sidestepped by
    /// pointing at somewhere else entirely.
    /// </para>
    /// </summary>
    public static bool IsInsideRequestRoot(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        string full;
        string root;

        try
        {
            full = Path.GetFullPath(candidate);
            root = Path.GetFullPath(RequestRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !HasReparsePoint(full, root);
    }

    /// <summary>Walks up from <paramref name="path"/> to <paramref name="stopAt"/> looking for a link.</summary>
    private static bool HasReparsePoint(string path, string stopAt)
    {
        var current = path;

        while (!string.IsNullOrEmpty(current) &&
               !current.Equals(stopAt, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Not created yet, which is fine — the caller is about to create it.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot tell, so assume the worst.
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? string.Empty;
        }

        return false;
    }
}
