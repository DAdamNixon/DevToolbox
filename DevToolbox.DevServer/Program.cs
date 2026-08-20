using DevToolbox.DevServer;
using DevToolbox.Services.Interfaces;
using DevToolbox.UI;
using Microsoft.Extensions.FileProviders;

// A browser-based host for the DevToolbox UI, for development only. The real app runs the same
// components inside a WinForms BlazorWebView (see DevToolbox.UI.Program); this serves them over
// an interactive server circuit so the views can be inspected, screenshotted and iterated on in
// a browser. Root.razor is the document shell, CatchAll.razor gives every URL a route, and the
// legacy App component runs as an interactive island with its own Router.

// Always Development: this host only exists for development, and it makes startup and circuit
// errors surface in full instead of as generic messages.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environments.Development,
});

// Fail at startup if a singleton ever captures a scoped service again — the WinForms host
// validates the same way. Explicit rather than left to the Development-environment
// defaults, so the check survives a change of environment.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddDevToolboxApp();

var app = builder.Build();

// The UI project's wwwroot, served at the root so index.html's relative asset paths (css/…,
// js/…) resolve identically in both hosts — and straight from the source tree, so a stylesheet
// edit shows up on refresh without a rebuild. Running from source is a given for a dev tool.
var uiWwwroot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "DevToolbox.UI", "wwwroot"));
if (!Directory.Exists(uiWwwroot))
{
    throw new InvalidOperationException(
        $"DevToolbox.UI/wwwroot not found at '{uiWwwroot}'. Run the dev server from the repository via " +
        "'dotnet run --project DevToolbox.DevServer'.");
}

app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uiWwwroot) });

// The framework script (_framework/blazor.web.js) is only served through the static assets
// endpoints in .NET 10 — UseStaticFiles alone no longer surfaces it (dotnet/aspnetcore#66059).
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<Root>().AddInteractiveServerRenderMode();

// Same startup work MainWindow_Load does, minus the tray icon, so Host Changer and Service
// Pulse show live data rather than their empty states. Failures are tolerated for the same
// reason as there: a bad config or unreadable hosts file must not stop the UI from opening,
// and the tabs surface those errors themselves.
_ = Task.Run(async () =>
{
    try
    {
        await app.Services.GetRequiredService<IHostsFileService>().InitializeAsync();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        app.Logger.LogWarning("Host Changer failed to start: {Message}", ex.Message);
    }

    try
    {
        await app.Services.GetRequiredService<IHealthMonitoringService>().InitializeAsync();
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogWarning("Service Pulse failed to start: {Message}", ex.Message);
    }
});

app.Run();
