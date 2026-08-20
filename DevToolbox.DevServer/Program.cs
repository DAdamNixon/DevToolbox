using DevToolbox.Services.Interfaces;
using DevToolbox.UI.Web;
using Microsoft.Extensions.DependencyInjection;

// A headless launcher for the browser view.
//
// The WinForms app hosts the same server itself and has done since it starts one on
// launch, so this is not how the feature ships — it exists for working on the UI
// without a desktop session or a WebView in the way. The host, the shell document and
// the routing all come from DevToolbox.UI.Web so there is one definition of them.

var port = args.Length > 0 && int.TryParse(args[0], out var requested)
    ? requested
    : WebPreviewHost.DefaultPort;

var web = WebPreviewHost.Build(new WebPreviewInfo(), port);
await web.StartAsync();

if (!web.IsRunning)
{
    Console.Error.WriteLine($"Could not start: {web.StartError}");
    return 1;
}

Console.WriteLine($"DevToolbox UI at {web.Url}");

// The startup work MainWindow_Load does, minus the tray icon, so Host Changer and
// Service Pulse show live data rather than their empty states. Failures are tolerated
// for the same reason as there: a bad config or an unreadable hosts file must not stop
// the UI opening, and those tabs surface the error themselves.
try
{
    await web.Services.GetRequiredService<IHostsFileService>().InitializeAsync();
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"Host Changer failed to start: {ex.Message}");
}

try
{
    await web.Services.GetRequiredService<IHealthMonitoringService>().InitializeAsync();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Service Pulse failed to start: {ex.Message}");
}

await Task.Delay(Timeout.Infinite);
return 0;
