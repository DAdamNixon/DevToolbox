using DevToolbox.Services;
using DevToolbox.Services.Interfaces;
using DevToolbox.UI.Web;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using PhotinoX.App;

namespace DevToolbox.UI.Linux;

/// <summary>
/// DevToolbox in a native window: GTK with a WebKitGTK view, through PhotinoX. The Linux counterpart
/// of the Windows host's MainWindow, and like it, it shows <see cref="App"/> from DevToolbox.UI.Shared
/// and nothing of its own.
/// <para>
/// Spike (phase 1 of the Photino plan): this owns the singletons by itself and the browser view does
/// not run beside it. Phase 2 makes it the owner that <see cref="WebPreviewHost"/> borrows from, as on
/// Windows.
/// </para>
/// </summary>
internal static class PhotinoWindow
{
    public static int Run()
    {
        var builder = PhotinoBlazorApp.CreateBuilder(new PhotinoAppOptions
        {
            // Not the working directory: the launcher starts it from wherever, and wwwroot/, the
            // static asset manifest and appsettings.json sit beside the executable.
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
        });

        // As on Windows: a singleton that captures a scoped service fails here, at startup.
        builder.ConfigureContainer(
            new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }),
            _ => { });

        builder.Services.AddDevToolboxApp();
        builder.Services.AddUnixPlatform();
        // Settings reads the browser view's address from this; empty until phase 2 starts one.
        builder.Services.AddSingleton(new WebPreviewInfo());

        builder.RootComponents.Add<App>("#app");
        builder.UseFileProvider(_ => WebRoot.Files());

        // DEVTOOLBOX_DEVTOOLS=1: the inspector on right-click, and the page's console in the terminal.
        var devTools = Environment.GetEnvironmentVariable("DEVTOOLBOX_DEVTOOLS") == "1";

        builder.ConfigureMainWindow(window => window
            .SetTitle("DevToolbox")
            .SetSize(1600, 900)
            // Only DevToolbox's own pages load here, and none of them needs more than a browser tab
            // would allow. Photino's defaults are looser, so these are said outright.
            .SetFileSystemAccessEnabled(false)
            .SetWebSecurityEnabled(true)
            .SetDevToolsEnabled(devTools)
            .SetBrowserControlInitParameters(devTools ? """{ "enable_write_console_messages_to_stdout": true }""" : ""));

        using var app = builder.Build();

        // The same start Program gives the browser-window path, so the tabs have live data.
        try
        {
            app.Services.GetRequiredService<IHostsFileService>().InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Host Changer failed to start: {ex.Message}");
        }

        try
        {
            var monitoring = app.Services.GetRequiredService<IHealthMonitoringService>();
            monitoring.ServiceAlertRaised += (_, e) => Notifications.ServiceAlert(e);
            monitoring.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Service Pulse failed to start: {ex.Message}");
        }

        return app.Run();
    }
}
