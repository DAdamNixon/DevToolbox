namespace DevToolbox.Services.Services;

/// <summary>
/// Copies files that ship beside the executable into a writable folder the user owns, for files that
/// folder does not have yet.
/// <para>
/// Shared by <see cref="ConfigDefaults"/> and <see cref="ScriptLibrary"/> because the rule is the
/// same for both and it is not an obvious rule: **never overwrite**. A file that exists is the user's,
/// however it got there, and an upgrade — which runs this again every time — must not throw away a
/// hand-edited file to put a stale shipped copy back.
/// </para>
/// </summary>
internal static class BundledFiles
{
    /// <param name="sourceDirectory">Where the shipped files are. Absent is normal for a plain build
    /// or a clone, and means there is nothing to do.</param>
    /// <param name="targetDirectory">The live folder. Created if needed.</param>
    /// <param name="extension">Which files to take, including the dot.</param>
    /// <returns>How many files were copied. Zero on any failure — seeding is a convenience, and must
    /// never be the reason the application cannot start.</returns>
    internal static int CopyMissing(string? sourceDirectory, string? targetDirectory, string extension)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory)) return 0;

        var copied = 0;

        try
        {
            if (!Directory.Exists(sourceDirectory)) return 0;

            Directory.CreateDirectory(targetDirectory);

            foreach (var source in Directory.EnumerateFiles(sourceDirectory))
            {
                if (!source.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;

                var destination = Path.Combine(targetDirectory, Path.GetFileName(source));
                if (File.Exists(destination)) continue;

                try
                {
                    File.Copy(source, destination);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One file that cannot be written must not stop the rest.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return copied;
    }
}
