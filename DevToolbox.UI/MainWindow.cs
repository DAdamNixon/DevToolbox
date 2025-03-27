using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using DevToolbox.Services;

namespace DevToolbox.UI
{
    public partial class MainWindow : Form
    {
        public MainWindow()
        {
            // Set DPI awareness for better scaling
            SetProcessDPIAware();
            
            InitializeComponent();
            
            // Set minimum window size
            this.MinimumSize = new Size(1600, 900);
            
            // Enable automatic DPI scaling
            this.AutoScaleMode = AutoScaleMode.Dpi;
            
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddBlazorWebViewDeveloperTools();
            
            // Register our services
            services.AddSingleton<PowerShellService>();
            
            blazorWebView1.HostPage = "wwwroot\\index.html";
            blazorWebView1.Services = services.BuildServiceProvider();
            blazorWebView1.RootComponents.Add<App>("#app");
        }

        private void MainWindow_Load(object sender, EventArgs e)
        {
            // Initialize any additional resources here if needed
        }
        
        // DPI awareness for Windows
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}