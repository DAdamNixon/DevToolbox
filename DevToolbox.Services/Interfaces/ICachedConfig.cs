namespace DevToolbox.Services.Interfaces;

/// <summary>
/// A service that reads one config file once and keeps it for the life of the process.
/// <para>
/// Caching is the right thing for these — the dashboard asks which program opens a file on every
/// click, and which icon a card wears on every render, and neither can afford a disk read. But it
/// means anything that changes the file behind the service's back is invisible until the
/// application is restarted, and <see cref="Services.ConfigRestore"/> does exactly that: it copies
/// a shipped file over the live one, without going through the service that owns it.
/// </para>
/// <para>
/// That was a real failure and not a theoretical one. Restoring <c>openHandlers.yaml</c> reported
/// success, told the user to reopen the tab, and changed nothing — the tab reopened onto the same
/// cached snapshot. The restore looked broken while working perfectly.
/// </para>
/// </summary>
public interface ICachedConfig
{
    /// <summary>
    /// The file this service reads, without the extension — the same key it passes to
    /// <see cref="IYamlStorageService"/>, so a restored <c>openHandlers.yaml</c> can be matched
    /// back to the service holding it.
    /// </summary>
    string ConfigKey { get; }

    /// <summary>
    /// Drops what is loaded, so the next read comes from disk. Safe to call when nothing is
    /// loaded yet.
    /// </summary>
    void Invalidate();
}
