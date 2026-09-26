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
//   devtoolbox --browser    start in a Chrome app window instead of DevToolbox's own
//   devtoolbox --no-window  start the server only, for a login autostart
//   devtoolbox --quit       stop the running copy
//   devtoolbox projects …   the command line (DevToolbox.Cli): find and open projects
//   devtoolbox logs …       the command line: search logs
//
// Shaped like the Windows app: DevToolbox's own window (PhotinoX, WebKitGTK) owns the services, and
// the browser view at localhost:5218 runs beside it for as long as the app does. linux.yaml's openIn,
// or --browser, opens a Chrome app window instead, as before; closing that window does not stop
// DevToolbox, and --quit or the launcher's Quit action does. Closing DevToolbox's own window quits,
// until it has a tray icon to hide in.

// The command line needs no server and no lock, so it runs beside a running window.
if (args.Length > 0 && DevToolbox.Cli.Cli.Verbs.Contains(args[0]))
{
    return await DevToolbox.Cli.Cli.RunAsync(args);
}

// Help, and anything this does not know, before anything starts: a typo must not open a window.
string[] windowSwitches = ["--no-window", "--quit", "--browser"];
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

// From here on, nothing awaits: GTK runs on the thread that built the window, and that is this one.

// Search results are scratch: the Log Viewer rebuilds its table on every search and never reads
// yesterday's rows. Thrown away before any service can open the database, which is safe only
// because the check above means no other copy is running.
LogDatabase.Reset();

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var info = new WebPreviewInfo();

// DevToolbox's own window unless asked for the browser, and the browser if the window cannot open.
PhotinoWindow? window = null;
if (openWindow && !args.Contains("--browser") && !LinuxSettings.Load().OpensInBrowser)
{
    if (PhotinoWindow.Unavailable() is { } reason)
    {
        Console.Error.WriteLine($"Opening DevToolbox in the browser instead of its own window: {reason}.");
    }
    else
    {
        window = PhotinoWindow.Create(configuration, info);
    }
}

// Disposed last, after the browser view that borrows its singletons has stopped.
using var owner = window;

// Runs until the window closes, --quit, Ctrl+C, or the session ending. Listening from here, before
// anything starts: a --quit that lands while Service Pulse is still starting would otherwise reach only
// ASP.NET's own handler, which stops the server and leaves the window open with nothing behind it.
var stopped = new TaskCompletionSource();
void Stop()
{
    stopped.TrySetResult();
    window?.Close();
}

using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; Stop(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };

// The browser view borrows the window's singletons, as on Windows. Without a window it owns them,
// and so it is the one that registers the platform's.
var web = WebPreviewHost.Build(info, owner: window?.Services, configureServices: services =>
{
    services.AddSingleton<IConfiguration>(configuration);
    if (window is null) services.AddUnixPlatform();
});

// Never throws. Without a window the server is the whole app, so it has to start; beside a window
// it is the browser view, which the window can do without, as on Windows, where Settings says why.
web.StartAsync().GetAwaiter().GetResult();
if (!web.IsRunning)
{
    Console.Error.WriteLine($"DevToolbox could not start its server: {web.StartError}");
    if (window is null) return 1;
}

instance.Publish(web.Url);
if (web.IsRunning) Console.WriteLine($"DevToolbox is running at {web.Url}");

var services = window?.Services ?? web.Services;

// What the Windows window does in its Load: Host Changer and Service Pulse start with the app rather
// than with their tabs, so the tabs have live data and history the first time they are opened. A
// bad config or an unreadable hosts file must not stop the app; those tabs show the error themselves.
try
{
    services.GetRequiredService<IHostsFileService>().InitializeAsync().GetAwaiter().GetResult();
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"Host Changer failed to start: {ex.Message}");
}

try
{
    var monitoring = services.GetRequiredService<IHealthMonitoringService>();
    monitoring.ServiceAlertRaised += (_, e) => Notifications.ServiceAlert(e);
    monitoring.InitializeAsync().GetAwaiter().GetResult();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Service Pulse failed to start: {ex.Message}");
}

if (window is not null)
{
    window.Run();
}
else
{
    if (openWindow) AppWindow.Open(web.Url!);
    stopped.Task.GetAwaiter().GetResult();
}

Debug.WriteLine("Stopping.");
web.StopAsync().GetAwaiter().GetResult();
return 0;
