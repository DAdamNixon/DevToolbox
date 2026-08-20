using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using DevToolbox.Services;
using DevToolbox.UI.Services;
using System.Diagnostics;

namespace DevToolbox.UI
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // Handled before anything else exists. Started with the "runas" verb, this instance
            // performs one hosts-file write (or one permission change) and exits — no window, no
            // WebView, no services. Keeping the elevated path this small is the point: it is the
            // only code in DevToolbox that ever runs as administrator.
            if (HostsElevatedCommands.TryGetRequestDirectory(args, out var hostsRequest))
            {
                return HostsElevatedCommands.Execute(hostsRequest);
            }

            // Then, before anything is built or started: is one of these already running? Deliberately
            // after the branch above — the elevated hosts write is a short-lived child of this same
            // executable, and must always be allowed to run alongside the window that launched it.
            //
            // Held for the life of the process. A second launch does not get here: it asks this
            // instance to show itself and exits, which is the whole fix. Without it, clicking the
            // shortcut while the window was hidden to the tray looked like nothing happened and
            // quietly started a second health monitor, hosts watcher and logs.db writer.
            if (!SingleInstance.TryAcquire(out var singleInstance))
            {
                return 0;
            }

            using (singleInstance)
            {
                return Run();
            }
        }

        private static int Run()
        {
            // Search results are scratch: the Log Viewer rebuilds its table on every search and never
            // reads yesterday's rows, but nothing ever deleted them either - the database had reached
            // 19.5 GB. Thrown away here, before any service can open it, and safe to do precisely
            // because the guard above means no second copy is running.
            var reclaimed = LogDatabase.Reset();
            if (reclaimed > 0)
            {
                Debug.WriteLine($"Discarded {reclaimed / 1024 / 1024} MB of stale log search data.");
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            
            // Build configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
            
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddBlazorWebViewDeveloperTools();

            // Register configuration
            services.AddSingleton<IConfiguration>(configuration);

            // Everything the components need, shared with the browser-based dev server.
            services.AddDevToolboxApp();

            // Disposed on the way out: the Host Changer's singleton owns a FileSystemWatcher and a
            // timer loop, and letting the provider go without disposing would leak both. Validated
            // the same way the dev server's provider is, so a singleton that captures a scoped
            // service fails here at startup instead of resurfacing as a stale-instance bug.
            using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            Application.Run(new MainWindow(serviceProvider));

            return 0;
        }
    }
}