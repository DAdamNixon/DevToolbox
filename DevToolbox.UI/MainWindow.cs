using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevToolbox.Services;
using DevToolbox.Services.Interfaces;

namespace DevToolbox.UI
{
    public partial class MainWindow : Form
    {
        private readonly IServiceProvider _serviceProvider;

        public MainWindow(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;

            // Set DPI awareness for better scaling
            SetProcessDPIAware();

            InitializeComponent();

            // Set minimum window size
            this.MinimumSize = new Size(1600, 900);

            // Enable automatic DPI scaling
            this.AutoScaleMode = AutoScaleMode.Dpi;

            blazorWebView1.HostPage = "wwwroot\\index.html";
            blazorWebView1.Services = serviceProvider;
            blazorWebView1.RootComponents.Add<App>("#app");

            this.Icon = Properties.Resources.toolbox_icon;
        }

        private void MainWindow_Load(object sender, EventArgs e)
        {
            // Service Pulse starts here rather than from its own tab, so endpoints
            // are being polled from launch and the tab has history to show the first
            // time it is opened. Previously monitoring only ever began if someone
            // pressed a button on that page, which is the main reason it appeared to
            // do nothing.
            _ = StartHealthMonitoringAsync();
        }

        private async Task StartHealthMonitoringAsync()
        {
            try
            {
                await _serviceProvider.GetRequiredService<IHealthMonitoringService>().InitializeAsync();
            }
            catch (InvalidOperationException ex)
            {
                // A bad config must not stop the app from opening; the Service Pulse
                // tab surfaces the same failure through ConfigError.
                Debug.WriteLine($"Service Pulse failed to start: {ex.Message}");
            }
        }
        
        // DPI awareness for Windows
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}