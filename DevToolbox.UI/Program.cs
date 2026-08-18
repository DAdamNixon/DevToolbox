using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using DevToolbox.Services;
using DevToolbox.UI.Services;

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

            // Register services
            services.AddScoped<IWorkspaceService, WorkspaceService>();
            services.AddScoped<IWorkspaceSourceService, WorkspaceSourceService>();
            services.AddScoped<IOpenHandlerService, OpenHandlerService>();
            services.AddScoped<IIconService, IconService>();
            services.AddScoped<ISystemService, SystemService>();
            services.AddScoped<DirectoryStructureService>();
            services.AddScoped<DevToolbox.Services.Services.PowerShellService>();
            services.AddScoped<DevToolbox.UI.Services.CardStateService>();
            services.AddScoped<IConfigurationService, ConfigurationService>();
            services.AddScoped<IScriptExecutionService, ScriptExecutionService>();
            // Singleton, not scoped: YAML storage is stateless and both singletons
            // below depend on it, and a singleton holding a scoped dependency is a
            // captive that outlives its own scope.
            services.AddSingleton<IYamlStorageService, YamlStorageService>();
            services.AddSingleton<IUiSettingsService, UiSettingsService>();
            services.AddScoped<ILogFileService, DbLogService>();
            services.AddScoped<ILogStorageService, SqliteLogStorageService>();

            // Owns background monitor loops that must outlive any one page, and is
            // started from MainWindow rather than from the Service Pulse tab. As a
            // scoped service its Dispose tore down every timer whenever a scope
            // ended, with nothing left to restart them.
            services.AddSingleton<IHealthMonitoringService, HealthMonitoringService>();

            // Singletons for the same reason: the Host Changer's file watcher and poll loop have to
            // outlive any one page, and the tray icon and the tab must agree about what is switched
            // on — which they only can if they share one snapshot.
            services.AddSingleton<IHostsSettingsService, HostsSettingsService>();
            services.AddSingleton<IHostsBackupService, HostsBackupService>();
            services.AddSingleton<IHostsWriteBroker, HostsWriteBroker>();
            services.AddSingleton<IHostsPermissionService, HostsPermissionService>();
            services.AddSingleton<IHostsFileService, HostsFileService>();

            // The seam between the tray icon, which is Windows Forms, and the Blazor router.
            services.AddSingleton<AppShellService>();

            // Register UI-specific services
            services.AddScoped<ViewModelFactory>();
            services.AddScoped<ModalStateService>();
            services.AddScoped<LogSearchStateService>();

            // Disposed on the way out: the Host Changer's singleton owns a FileSystemWatcher and a
            // timer loop, and letting the provider go without disposing would leak both.
            using var serviceProvider = services.BuildServiceProvider();
            Application.Run(new MainWindow(serviceProvider));

            return 0;
        }
    }
}