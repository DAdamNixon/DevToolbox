namespace DevToolbox.Services.Services;

/// <summary>
/// Copies the YAML shipped next to the executable into the user's config folder, for files the user
/// does not have yet.
/// <para>
/// This exists because an installer cannot do it. MSIX has no custom install actions by design —
/// there is no script step that could drop files into <c>%LOCALAPPDATA%</c> — so a package that
/// wants to arrive pre-configured has to hand its configuration to the application and let the
/// application place it on first run. Everything about which files those are lives in the package,
/// not here: this copies whatever it finds, so the application stays agnostic and an installer for
/// any site can seed its own.
/// </para>
/// <para>
/// Never overwrites. A file that exists is the user's, however it got there, and a reinstall or an
/// upgrade must not throw away a hand-edited config to put a stale default back.
/// </para>
/// </summary>
public static class ConfigDefaults
{
    /// <summary>The folder an installer puts its bundled YAML in, alongside the executable.</summary>
    public const string FolderName = "ConfigDefaults";

    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Seeds from the folder beside the running executable. Returns how many files were copied.</summary>
    public static int SeedInto(string configDirectory) => SeedFrom(SourceDirectory, configDirectory);

    /// <param name="sourceDirectory">Where the bundled defaults are. Absent is the normal case for a
    /// plain build or a clone, and means there is nothing to do.</param>
    /// <param name="configDirectory">The live config folder. Created if needed.</param>
    /// <returns>How many files were copied. Zero on any failure — seeding is a convenience, and must
    /// never be the reason the application cannot start.</returns>
    public static int SeedFrom(string sourceDirectory, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(configDirectory)) return 0;

        var copied = 0;

        try
        {
            if (!Directory.Exists(sourceDirectory)) return 0;

            Directory.CreateDirectory(configDirectory);

            foreach (var source in Directory.EnumerateFiles(sourceDirectory))
            {
                if (!source.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

                var destination = Path.Combine(configDirectory, Path.GetFileName(source));
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
