namespace DevToolbox.UI.Linux;

/// <summary>
/// DevToolbox's tray icon, whatever draws it: the counterpart of the Windows app's HostsTrayIcon.
/// <para>
/// A seam inside the host, so that macOS can have an <c>NSStatusItem</c> version later without the
/// window or Program knowing which one they have.
/// </para>
/// </summary>
internal interface ITrayIcon : IDisposable
{
    /// <summary>
    /// Whether the desktop is showing the icon right now. Hiding the window into a tray nobody can see
    /// would leave DevToolbox running with no way back but a second launch, so closing the window
    /// quits instead when this is false.
    /// </summary>
    bool IsShowing { get; }
}
