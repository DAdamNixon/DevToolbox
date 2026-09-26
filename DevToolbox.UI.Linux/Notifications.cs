using System.ComponentModel;
using System.Diagnostics;
using DevToolbox.Services.Interfaces;

namespace DevToolbox.UI.Linux;

/// <summary>
/// Service Pulse's alerts as desktop notifications — what the tray balloons are on Windows.
/// <c>notify-send</c> on Linux, which every desktop's notification daemon answers.
/// </summary>
internal static class Notifications
{
    public static void ServiceAlert(ServiceAlertEventArgs e)
    {
        if (e.IsRecovery)
        {
            Show($"{e.ServiceName} is back online", "Service Pulse", urgency: "normal");
        }
        else
        {
            Show($"{e.ServiceName} is down", $"{e.ConsecutiveFailures} consecutive failed pings — Service Pulse", urgency: "critical");
        }
    }

    /// <summary>
    /// Closing the window hid it rather than quitting: said once, the first time, as the Windows app's
    /// balloon does.
    /// </summary>
    public static void HiddenToTray() =>
        Show("DevToolbox is still running", "Use its icon in the top bar to switch hosts, show the window, or exit.");

    public static void Show(string title, string body, string urgency = "normal")
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("osascript") { ArgumentList = { "-e", $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"" } }
                : new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=DevToolbox", $"--urgency={urgency}", title, body } };

            startInfo.UseShellExecute = false;
            using var _ = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            // No notifier installed. The alert is still on the Service Pulse tab.
        }

        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
