using Microsoft.Extensions.DependencyInjection;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using DevToolbox.UI.Services;

namespace DevToolbox.UI
{
    /// <summary>
    /// Every service the Blazor components depend on, registered once for both hosts:
    /// the Windows Forms window (<see cref="Program"/>) and the browser view
    /// (<see cref="Web.WebPreviewHost"/>).
    /// <para>
    /// The two hosts need separate containers and cannot be talked out of it: Blazor
    /// Server and BlazorWebView each register their own <c>NavigationManager</c> — among
    /// other core services — and whichever registers last wins, so a single shared
    /// container hands the WebView a <c>RemoteNavigationManager</c> and it fails on the
    /// cast. What they must not have is two sets of the application's own singletons,
    /// which would mean two health monitors, two hosts-file watchers and two writers on
    /// logs.db. Hence the split below: one host owns the singletons and the other
    /// borrows them.
    /// </para>
    /// </summary>
    public static class ServiceRegistration
    {
        /// <summary>Everything, with this container owning the singletons.</summary>
        public static IServiceCollection AddDevToolboxApp(this IServiceCollection services) =>
            services.AddOwnedSingletons().AddPerHostServices();

        /// <summary>
        /// Everything, with the singletons borrowed from <paramref name="owner"/> rather
        /// than created here — for the second host in a process that already has one.
        /// </summary>
        public static IServiceCollection AddDevToolboxApp(this IServiceCollection services, IServiceProvider owner) =>
            services.AddBorrowedSingletons(owner).AddPerHostServices();

        /// <summary>
        /// State that belongs to one host: a page's view models, the open-menu tracker,
        /// the Log Viewer's in-flight search. Two hosts each want their own.
        /// </summary>
        private static IServiceCollection AddPerHostServices(this IServiceCollection services)
        {
            services.AddScoped<IWorkspaceService, WorkspaceService>();
            services.AddScoped<IWorkspaceSourceService, WorkspaceSourceService>();
            services.AddScoped<IIconService, IconService>();
            services.AddScoped<CardStateService>();
            services.AddScoped<IConfigurationService, ConfigurationService>();
            services.AddScoped<IScriptExecutionService, ScriptExecutionService>();
            services.AddScoped<ILogFileService, DbLogService>();
            services.AddScoped<ILogStorageService, SqliteLogStorageService>();
            // Reads and writes the same YAML DbLogService reads. Scoped alongside it because it holds
            // nothing between calls — every method goes to the file.
            services.AddScoped<ILogConfigService, LogConfigService>();

            // Register UI-specific services
            services.AddScoped<ViewModelFactory>();
            services.AddScoped<ModalStateService>();
            // Shared, because the point of it is that opening one menu closes every other — which
            // it can only do if every card is looking at the same instance.
            services.AddScoped<MenuStateService>();
            services.AddScoped<LogSearchStateService>();

            return services;
        }

        /// <summary>
        /// The single-instance services. Each owns something there must only be one of:
        /// a file watcher, a timer loop, a cached config snapshot.
        /// </summary>
        private static IServiceCollection AddOwnedSingletons(this IServiceCollection services)
        {
            // Singletons because the Host Changer's HostsFileService singleton below takes all
            // three in its constructor, and a singleton's dependency is resolved once and kept —
            // registered as scoped it would be a captive that outlives its scope. All three are
            // safe to share: PowerShellService and SystemService hold no per-use state, and
            // OpenHandlerService caches one config snapshot the way UiSettingsService and
            // HostsSettingsService already do — shared, so a handler saved on the dashboard is
            // also seen by the hosts file's Open button.
            services.AddSingleton<PowerShellService>();
            services.AddSingleton<ISystemService, SystemService>();
            services.AddSingleton<IOpenHandlerService, OpenHandlerService>();

            // Singleton, not scoped: YAML storage is stateless and both singletons
            // below depend on it, and a singleton holding a scoped dependency is a
            // captive that outlives its own scope.
            services.AddSingleton<IYamlStorageService, YamlStorageService>();
            services.AddSingleton<IUiSettingsService, UiSettingsService>();

            // Singleton for the reason named at the top of this method: it holds a cached config
            // snapshot, and every writer of dashboardLayout.yaml writes the whole file from that
            // snapshot. Two of them means last-writer-wins — and there are two ways to get two.
            // The obvious one is the app's two hosts. The less obvious one is that Blazor Server
            // builds a fresh DI scope per circuit, so a page reload gives the Projects tab a second
            // cache while the outgoing circuit is still alive, and a pin or a drag from either can
            // land on top of the other's file. One instance, one snapshot, no race.
            services.AddSingleton<IDashboardLayoutService, DashboardLayoutService>();

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

            return services;
        }

        /// <summary>
        /// The same set, resolved from the owning container and registered here as
        /// instances. The instance overload matters: a container disposes singletons it
        /// built itself, so registering these by factory would let the borrowing host
        /// dispose the owner's file watcher and monitor timers when it shuts down.
        /// </summary>
        private static IServiceCollection AddBorrowedSingletons(this IServiceCollection services, IServiceProvider owner)
        {
            services.Borrow<PowerShellService>(owner);
            services.Borrow<ISystemService>(owner);
            services.Borrow<IOpenHandlerService>(owner);
            services.Borrow<IYamlStorageService>(owner);
            services.Borrow<IUiSettingsService>(owner);
            services.Borrow<IDashboardLayoutService>(owner);
            services.Borrow<IHealthMonitoringService>(owner);
            services.Borrow<IHostsSettingsService>(owner);
            services.Borrow<IHostsBackupService>(owner);
            services.Borrow<IHostsWriteBroker>(owner);
            services.Borrow<IHostsPermissionService>(owner);
            services.Borrow<IHostsFileService>(owner);
            services.Borrow<AppShellService>(owner);

            return services;
        }

        private static void Borrow<T>(this IServiceCollection services, IServiceProvider owner) where T : class =>
            services.AddSingleton(owner.GetRequiredService<T>());
    }
}
