using Microsoft.Extensions.DependencyInjection;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using DevToolbox.UI.Services;

namespace DevToolbox.UI
{
    /// <summary>
    /// Every service the Blazor components depend on, registered once for both hosts: the real
    /// Windows Forms app (<see cref="Program"/>) and the browser-based dev server
    /// (DevToolbox.DevServer), which exists so the UI can be viewed and iterated on in an
    /// ordinary browser. Keeping the list here means a service added for a component cannot be
    /// present in one host and missing in the other.
    /// </summary>
    public static class ServiceRegistration
    {
        public static IServiceCollection AddDevToolboxApp(this IServiceCollection services)
        {
            services.AddScoped<IWorkspaceService, WorkspaceService>();
            services.AddScoped<IWorkspaceSourceService, WorkspaceSourceService>();
            services.AddScoped<IIconService, IconService>();

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

            services.AddScoped<CardStateService>();
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
            // Shared, because the point of it is that opening one menu closes every other — which
            // it can only do if every card is looking at the same instance.
            services.AddScoped<MenuStateService>();
            services.AddScoped<LogSearchStateService>();

            return services;
        }
    }
}
