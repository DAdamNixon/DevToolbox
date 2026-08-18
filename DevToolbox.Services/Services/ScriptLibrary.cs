namespace DevToolbox.Services.Services;

/// <summary>
/// Where PowerShell scripts live, and how the ones that ship with the application get there.
/// <para>
/// They used to live in <c>Scripts</c> beside the executable, which three services each worked out
/// for themselves. That is fine for a build you run out of <c>bin</c> and broken everywhere else: an
/// installed package sits under <c>C:\Program Files\WindowsApps</c>, which is **read-only**, so New
/// Script, Save and Delete all failed for every installed user — and anything a user did manage to
/// add would have been destroyed by the next upgrade, since an upgrade replaces that folder wholesale.
/// The Scripts tab was the only part of the application that had ever tried to write there, which is
/// why nothing else showed the problem. The workspace card's *Run Script* menu only reads, so it
/// worked, and hid it.
/// </para>
/// <para>
/// So scripts live where the YAML does — under <c>%LOCALAPPDATA%\DevToolbox</c> — and the shipped
/// ones are copied in on first run, exactly as <see cref="ConfigDefaults"/> does for configuration
/// and for the same reason: MSIX has no install script that could put them there.
/// </para>
/// <para>
/// One place decides this now, and every service asks. Three copies of a path is how the tab ends up
/// editing a script the Run Script menu cannot see.
/// </para>
/// </summary>
public static class ScriptLibrary
{
    public const string FolderName = "Scripts";

    /// <summary>The scripts that ship with the application. Read-only once installed.</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>The user's scripts. Writable, and survives an upgrade.</summary>
    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevToolbox",
        FolderName);

    /// <summary>
    /// The directory every service should use: created if missing, and holding a copy of every
    /// shipped script this machine does not already have.
    /// </summary>
    public static string EnsureUserDirectory()
    {
        var directory = UserDirectory;

        try
        {
            // Before seeding rather than relying on it: with no bundled folder — a plain build that
            // never copied one — seeding does nothing, and the tab still needs somewhere to save to.
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing scripts will come back empty and saving will report a failure, which is a far
            // better outcome than refusing to start.
            return directory;
        }

        SeedFrom(BundledDirectory, directory);

        return directory;
    }

    /// <summary>
    /// Copies shipped scripts that are missing. Never overwrites: an edited script is the user's.
    /// </summary>
    /// <returns>How many were copied.</returns>
    public static int SeedFrom(string sourceDirectory, string scriptsDirectory) =>
        BundledFiles.CopyMissing(sourceDirectory, scriptsDirectory, ".ps1");
}
