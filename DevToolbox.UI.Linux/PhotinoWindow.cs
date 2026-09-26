using System.Runtime.InteropServices;
using DevToolbox.Services;
using DevToolbox.UI.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using PhotinoX.App;

namespace DevToolbox.UI.Linux;

/// <summary>
/// DevToolbox in a native window: GTK with a WebKitGTK view, through PhotinoX. The Linux counterpart
/// of the Windows host's MainWindow, and like it, it shows <see cref="App"/> from DevToolbox.UI.Shared
/// and nothing of its own.
/// <para>
/// Its container owns the application's singletons, as the Windows Forms container does on Windows,
/// and the browser view borrows them (<see cref="WebPreviewHost.Build"/> with an owner). So the window
/// and localhost:5218 show one Service Pulse, one hosts-file watcher and one writer on logs.db.
/// </para>
/// </summary>
internal sealed class PhotinoWindow : IDisposable
{
    private readonly PhotinoBlazorApp _app;

    private volatile bool _closeRequested;

    private PhotinoWindow(PhotinoBlazorApp app)
    {
        _app = app;

        // A Close that came before the loop was running, such as --quit while Service Pulse was still
        // starting: the Shutdown it posted had no loop to run on, so it is acted on here instead.
        _app.Application.RegisterStartupHandler((_, _) =>
        {
            if (_closeRequested) _app.Application.Shutdown(0, force: true);
        });
    }

    /// <summary>The owning container, for the browser view to borrow from.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Why the window cannot open here, or null if it can.
    /// <para>
    /// Asked before PhotinoX is touched, because its failures are not exceptions: with no display,
    /// its gtk_init ends the process, and without WebKitGTK the native library cannot load at all.
    /// </para>
    /// </summary>
    public static string? Unavailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return "there is no display";
        }

        if (!NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var webkit))
        {
            return "WebKitGTK 4.1 is not installed (on Ubuntu: sudo apt install libwebkit2gtk-4.1-0)";
        }

        NativeLibrary.Free(webkit);
        return null;
    }

    /// <summary>Builds the window and its container. Nothing is shown until <see cref="Run"/>.</summary>
    public static PhotinoWindow Create(IConfiguration configuration, WebPreviewInfo info)
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

        // The configuration the browser view is given too, so both containers read the same one.
        builder.Services.AddSingleton(configuration);
        builder.Services.AddDevToolboxApp();
        builder.Services.AddUnixPlatform();
        // One instance in both containers, so Settings shows the browser view's address in the window.
        builder.Services.AddSingleton(info);

        builder.RootComponents.Add<App>("#app");
        builder.UseFileProvider(_ => WebRoot.Files());

        // DEVTOOLBOX_DEVTOOLS=1: the inspector on right-click, the page's console in the terminal, and
        // PhotinoX's own log of what it is doing.
        var devTools = Environment.GetEnvironmentVariable("DEVTOOLBOX_DEVTOOLS") == "1";

        builder.ConfigureMainWindow(window => window
            .SetTitle("DevToolbox")
            .SetSize(1600, 900)
            .SetLogVerbosity(devTools ? 2 : 0)
            // Only DevToolbox's own pages load here, and none of them needs more than a browser tab
            // would allow. Photino's defaults are looser, so these are said outright.
            .SetFileSystemAccessEnabled(false)
            .SetWebSecurityEnabled(true)
            .SetDevToolsEnabled(devTools)
            .SetBrowserControlInitParameters(devTools ? """{ "enable_write_console_messages_to_stdout": true }""" : ""));

        return new PhotinoWindow(builder.Build());
    }

    /// <summary>Shows the window and runs until it is closed or <see cref="Close"/> is called.</summary>
    public int Run() => _closeRequested ? 0 : _app.Run();

    /// <summary>Closes the window and ends <see cref="Run"/>. Safe from any thread, such as a signal handler.</summary>
    public void Close()
    {
        _closeRequested = true;
        var application = _app.Application;
        application.Dispatcher.BeginInvoke(() => application.Shutdown(0, force: true));
    }

    /// <summary>Disposes the container, and with it the singletons: after the browser view has stopped.</summary>
    public void Dispose() => _app.Dispose();
}
