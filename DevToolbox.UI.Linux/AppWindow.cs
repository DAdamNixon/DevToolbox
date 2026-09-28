using System.ComponentModel;
using System.Diagnostics;

namespace DevToolbox.UI.Linux;

/// <summary>
/// Opens DevToolbox as a window of its own.
/// <para>
/// Chrome, Chromium, Edge and Brave all have an app mode — <c>--app=URL</c> — that opens a page in
/// a window with no tabs or address bar, which is as close to the Windows app's window as a browser
/// gets. With none of them installed, the default browser opens it in a tab instead.
/// </para>
/// </summary>
internal static class AppWindow
{
    private static readonly string[] AppModeBrowsers =
    [
        "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser",
    ];

    public static void Open(string url)
    {
        foreach (var browser in AppModeBrowsers)
        {
            if (TryStart(browser, [$"--app={url}", "--class=DevToolbox"])) return;
        }

        if (!TryStart(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url]))
        {
            Console.Error.WriteLine($"Could not open a browser. DevToolbox is at {url}");
        }
    }

    /// <summary>The browser view in the default browser, as a tab: the tray's "Open in browser".</summary>
    public static void OpenInBrowser(string url)
    {
        if (!TryStart(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url]))
        {
            Console.Error.WriteLine($"Could not open a browser. DevToolbox is at {url}");
        }
    }

    private static bool TryStart(string program, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            // The browser outlives this call and prints its own chatter; neither is ours to wait on.
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;

            using var _ = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // Not installed.
        }
    }
}
