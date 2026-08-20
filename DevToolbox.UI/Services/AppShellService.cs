namespace DevToolbox.UI.Services;

/// <summary>
/// Lets code outside the web view ask the app to show a page.
/// <para>
/// The tray icon lives in Windows Forms and has no <c>NavigationManager</c>; the router lives in
/// Blazor and knows nothing about a tray. This singleton is the seam between them:
/// <see cref="MainLayout"/> subscribes once and turns a request into a real navigation.
/// </para>
/// </summary>
public sealed class AppShellService
{
    /// <summary>
    /// Raised with a route such as <c>/host-changer</c>. Fires on whichever thread asked, so the
    /// subscriber marshals it onto the Blazor context itself.
    /// </summary>
    public event Action<string>? NavigationRequested;

    public void RequestNavigation(string route)
    {
        if (!string.IsNullOrWhiteSpace(route)) NavigationRequested?.Invoke(route);
    }
}
