using System.Diagnostics;
using DevToolbox.Services;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.UI.Linux;
using DevToolbox.UI.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// DevToolbox on Linux.
//
//   devtoolbox              start, or bring up the window of the copy that is already running
//   devtoolbox --no-window  start the server only, for a login autostart
//   devtoolbox --quit       stop the running copy
//   devtoolbox projects …   the command line (DevToolbox.Cli): find and open projects
//   devtoolbox logs …       the command line: search logs
//
// Closing the window does not stop DevToolbox, the same as closing the Windows window with the tray
// icon on: Service Pulse keeps polling and its alerts keep arriving as desktop notifications. The
// launcher's Quit action, or --quit, is the Exit in the tray menu.

// The command line needs no server and no lock, so it runs beside a running window.
if (args.Length > 0 && DevToolbox.Cli.Cli.Verbs.Contains(args[0]))
{
    return await DevToolbox.Cli.Cli.RunAsync(args);
}

// Help, and anything this does not know, before anything starts: a typo must not open a window.
// --photino is the Photino plan's phase 1 spike, left out of the usage until phase 2 makes it the default.
string[] windowSwitches = ["--no-window", "--quit", "--photino"];
if (args.Any(a => a is "-h" or "-?" or "--help" or "help"))
{
    return await Usage.PrintAsync();
}

if (args.Contains("--version"))
{
    // The same string the status bar and Settings show, e.g. 0.9.10.2-beta.20.
    Console.Out.WriteLine(DevToolbox.Services.AppVersion.Display);
    return 0;
}

if (args.FirstOrDefault(a => !windowSwitches.Contains(a)) is { } unknown)
{
    Console.Error.WriteLine($"Unknown argument: {unknown}");
    Console.Error.WriteLine();
    await Usage.PrintAsync();
    return 1;
}

if (args.Contains("--quit"))
{
    return Instance.Quit() ? 0 : 1;
}

var openWindow = !args.Contains("--no-window");

// One copy per user, for the reason the Windows app has one: two would mean two health monitors,
// two hosts-file watchers and two writers on logs.db. A second launch shows the first one's window.
using var instance = Instance.TryAcquire();
if (instance is null)
{
    if (!openWindow) return 0;

    if (Instance.RunningUrl() is { } url)
    {
        AppWindow.Open(url);
        return 0;
    }

    Console.Error.WriteLine("DevToolbox is already running, but its address could not be read. Try: devtoolbox --quit");
    return 1;
}

// Search results are scratch: the Log Viewer rebuilds its table on every search and never reads
// yesterday's rows. Thrown away before any service can open the database, which is safe only
// because the check above means no other copy is running.
LogDatabase.Reset();

if (args.Contains("--photino"))
{
    return PhotinoWindow.Run();
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var info = new WebPreviewInfo();
var web = WebPreviewHost.Build(info, configureServices: services =>
{
    services.AddSingleton<IConfiguration>(configuration);
    services.AddUnixPlatform();
});

await web.StartAsync();
if (!web.IsRunning)
{
    Console.Error.WriteLine($"DevToolbox could not start its server: {web.StartError}");
    return 1;
}

instance.Publish(web.Url!);
Console.WriteLine($"DevToolbox is running at {web.Url}");

// What the Windows window does in its Load: Host Changer and Service Pulse start with the app rather
// than with their tabs, so the tabs have live data and history the first time they are opened. A
// bad config or an unreadable hosts file must not stop the app; those tabs show the error themselves.
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
    var monitoring = web.Services.GetRequiredService<IHealthMonitoringService>();
    monitoring.ServiceAlertRaised += (_, e) => Notifications.ServiceAlert(e);
    await monitoring.InitializeAsync();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Service Pulse failed to start: {ex.Message}");
}

if (openWindow)
{
    AppWindow.Open(web.Url!);
}

// Until --quit, Ctrl+C, or the session ending.
var stopped = new TaskCompletionSource();
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stopped.TrySetResult(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };

await stopped.Task;
Debug.WriteLine("Stopping.");
await web.StopAsync();
return 0;
