using Microsoft.AspNetCore.Components.WebView.WindowsForms;
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
            services.AddSingleton<PowerShellService>();
            services.AddScoped<IConfigurationService, ConfigurationService>();
            services.AddScoped<IScriptExecutionService, ScriptExecutionService>();
            services.AddScoped<IYamlStorageService, YamlStorageService>();
            services.AddScoped<DirectoryStructureService>();
            services.AddScoped<IWorkspaceService, WorkspaceService>();
            services.AddScoped<CardStateService>();
            services.AddScoped<ModalService>();

            var serviceProvider = services.BuildServiceProvider();
            Application.Run(new MainWindow(serviceProvider));
        }
    }
}