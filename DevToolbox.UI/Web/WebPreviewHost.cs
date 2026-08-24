using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
        builder.WebHost.ConfigureKestrel(options => ListenOnLoopback(options, ChoosePort(preferredPort)));

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
    /// Binds both loopback stacks on <paramref name="port"/>.
    /// <para>
    /// Not <c>ListenLocalhost</c> unconditionally, because it rejects port 0 outright —
    /// "Dynamic port binding is not supported when binding to localhost. You must either
    /// bind to 127.0.0.1:0 or [::1]:0, or both." — and it throws that from
    /// <see cref="WebApplicationBuilder.Build"/>, before <see cref="StartAsync"/> exists
    /// to catch it. So a second instance, or a leftover dev server on the port, took the
    /// whole WinForms app down at launch rather than merely costing it the browser view.
    /// </para>
    /// </summary>
    private static void ListenOnLoopback(KestrelServerOptions options, int port)
    {
        if (port != 0)
        {
            options.ListenLocalhost(port);
            return;
        }

        // Last resort, when nothing in the scanned range was free. Each call gets its own
        // OS-assigned port, so the two stacks end up on different ones and Urls reports
        // both — which is why this is the fallback and not the normal path.
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.IPv6Loopback, 0);
    }

    /// <summary>
    /// The preferred port if it is free, else the next free one after it, else 0 for
    /// "let the OS decide".
    /// <para>
    /// Kestrel only discovers a clash when it binds, which happens inside StartAsync
    /// and takes the whole host down with it. Asking first turns "that port is taken"
    /// into a different port rather than no browser view at all.
    /// </para>
    /// <para>
    /// A concrete port is worth scanning for rather than passing 0 and taking whatever
    /// the OS hands out: the same number has to work on both loopback stacks, since
    /// "localhost" resolves to ::1 before 127.0.0.1 on Windows and the two would
    /// otherwise be assigned different ports.
    /// </para>
    /// </summary>
    private static int ChoosePort(int preferred)
    {
        // Ten is enough for the case this exists for — a couple of instances and a stale
        // dev server — without turning a launch into a port scan.
        for (var port = preferred; port < preferred + 10; port++)
        {
            if (IsFree(port)) return port;
        }

        return 0;
    }

    /// <summary>
    /// Whether both loopback stacks will take <paramref name="port"/>. Both, because
    /// <c>ListenLocalhost</c> binds both and fails if either refuses — probing only IPv4
    /// would hand back a port that then dies at bind time.
    /// </summary>
    private static bool IsFree(int port)
    {
        return CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.IPv6Loopback, port);

        static bool CanBind(IPAddress address, int port)
        {
            TcpListener? probe = null;
            try
            {
                probe = new TcpListener(address, port);
                probe.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                probe?.Stop();
            }
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
