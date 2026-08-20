using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevToolbox.UI.Web;

/// <summary>
/// The browser-facing half of the app: the same Blazor components the WinForms
/// window shows in its WebView, served over an interactive server circuit so they
/// can be opened in a real browser.
/// <para>
/// It is one process and one container. <see cref="Build"/> returns the
/// <see cref="WebApplication"/> whose <c>Services</c> the WinForms host then hands
/// to its own BlazorWebView, so both surfaces share a single set of singletons.
/// Standing up a second provider instead would mean a second health monitor, a
/// second hosts-file watcher and a second writer on logs.db — the exact class of
/// duplicate-background-work bug <see cref="SingleInstance"/> exists to prevent.
/// </para>
/// </summary>
public sealed class WebPreviewHost
{
    /// <summary>
    /// Stable so the URL is worth bookmarking. Taken from the range IANA leaves
    /// unassigned and ASP.NET templates draw from.
    /// </summary>
    public const int DefaultPort = 5218;

    private readonly WebApplication _app;

    private readonly WebPreviewInfo _info;

    private WebPreviewHost(WebApplication app, WebPreviewInfo info)
    {
        _app = app;
        _info = info;
    }

    /// <summary>Where the server ended up listening, once <see cref="StartAsync"/> has succeeded.</summary>
    public string? Url { get; private set; }

    /// <summary>Why the server is not listening, if it is not.</summary>
    public string? StartError { get; private set; }

    public bool IsRunning => Url is not null;

    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Builds the host without starting it. <paramref name="configureServices"/> is
    /// where a caller adds what only it needs — the WinForms host registers its
    /// WebView services here so they land in the shared container.
    /// </summary>
    public static WebPreviewHost Build(
        WebPreviewInfo info,
        int preferredPort = DefaultPort,
        IServiceProvider? owner = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Not the working directory: a WinForms app is launched from wherever the
            // shortcut points, and wwwroot/ and the static asset manifest sit beside
            // the executable.
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development,
        });

        // Loopback only, and deliberately so. This surface runs PowerShell scripts and
        // edits the hosts file; binding any other interface would put that on the network.
        // ListenLocalhost rather than a single address because "localhost" resolves to ::1
        // before 127.0.0.1 on Windows, and binding only IPv4 refuses the browser outright.
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(ChoosePort(preferredPort)));

        // Matches how the WinForms host has always built its provider, and catches a
        // singleton capturing a scoped service at startup rather than as a stale-state
        // bug much later.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        // Borrow the singletons when another host in this process already owns them,
        // so there is one health monitor and one hosts watcher, not two.
        if (owner is null) builder.Services.AddDevToolboxApp();
        else builder.Services.AddDevToolboxApp(owner);

        // The caller's instance, so the Windows Forms container and this one report the
        // same address from the same object.
        builder.Services.AddSingleton(info);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseStaticFiles();

        // The framework script (_framework/blazor.web.js) is only reachable through the
        // static asset endpoints in .NET 10; UseStaticFiles alone no longer surfaces it
        // (dotnet/aspnetcore#66059).
        app.MapStaticAssets();
        app.UseAntiforgery();
        app.MapRazorComponents<Root>().AddInteractiveServerRenderMode();

        return new WebPreviewHost(app, info);
    }

    /// <summary>
    /// The preferred port if it is free, otherwise 0 so the OS assigns one.
    /// <para>
    /// Kestrel only discovers a clash when it binds, which happens inside StartAsync
    /// and takes the whole host down with it. Asking first turns "that port is taken"
    /// into a different port rather than no browser view at all.
    /// </para>
    /// </summary>
    private static int ChoosePort(int preferred)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, preferred);
            probe.Start();
            probe.Stop();
            return preferred;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Starts listening. Never throws: the window has to open whether or not the
    /// browser half came up, so a failure is recorded in <see cref="StartError"/>
    /// for the Settings page to show.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            Url = _app.Urls.FirstOrDefault();
            StartError = Url is null ? "The server started but reported no address." : null;
            Publish();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpListenerException)
        {
            StartError = ex.Message;
            Publish();
            _app.Services.GetService<ILoggerFactory>()?
                .CreateLogger<WebPreviewHost>()
                .LogWarning("Browser preview failed to start: {Message}", ex.Message);
        }
    }

    /// <summary>Copies the outcome into the container so pages can read it.</summary>
    private void Publish()
    {
        _info.Url = Url;
        _info.Error = StartError;
    }

    public async Task StopAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down; nothing useful left to do about it.
        }
        finally
        {
            Url = null;
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
