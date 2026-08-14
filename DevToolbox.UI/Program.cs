using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
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
        static void Main()
        {
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

            // Register UI-specific services
            services.AddScoped<ViewModelFactory>();
            services.AddScoped<ModalStateService>();
            services.AddScoped<LogSearchStateService>();

            var serviceProvider = services.BuildServiceProvider();
            Application.Run(new MainWindow(serviceProvider));
        }
    }
}