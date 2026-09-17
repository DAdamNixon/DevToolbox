using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevToolbox.Services;
using DevToolbox.Services.Interfaces;
using DevToolbox.UI.Services;

namespace DevToolbox.UI
{
    public partial class MainWindow : Form
    {
        private readonly IServiceProvider _serviceProvider;

        private HostsTrayIcon? _hostsTray;

        /// <summary>
        /// Set by the tray's Exit item so <see cref="MainWindow_FormClosing"/> lets the close through
        /// instead of hiding the window again.
        /// </summary>
        private bool _exiting;

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

            // Same reasoning, and one more: the tray icon has to know which hosts
            // options are switched on before anybody opens the tab.
            _ = StartHostsWatchAsync();
        }

        private async Task StartHostsWatchAsync()
        {
            try
            {
                await _serviceProvider.GetRequiredService<IHostsFileService>().InitializeAsync();
                await CreateHostsTrayAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A missing or unreadable hosts file must not stop the app from opening; the Host
                // Changer tab surfaces the same failure through LoadError.
                Debug.WriteLine($"Host Changer failed to start: {ex.Message}");
            }
        }

        private async Task CreateHostsTrayAsync()
        {
            var settings = _serviceProvider.GetRequiredService<IHostsSettingsService>();
            if (!(await settings.GetAsync()).ShowTrayIcon) return;

            _hostsTray = new HostsTrayIcon(
                components,
                this,
                _serviceProvider.GetRequiredService<IHostsFileService>(),
                settings,
                _serviceProvider.GetRequiredService<AppShellService>(),
                ShowFromTray,
                ExitFromTray);
        }

        /// <summary>
        /// Answers the broadcast a second launch sends before exiting, so clicking the shortcut
        /// reopens this window rather than doing nothing. See <see cref="SingleInstance"/>.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            // The zero check is not redundant: RegisterWindowMessage returns 0 on failure, and a
            // great many ordinary messages have id 0 — matching on it would fire this constantly.
            if (SingleInstance.ShowWindowMessage != 0 && m.Msg == SingleInstance.ShowWindowMessage)
            {
                ShowFromTray();
            }

            base.WndProc(ref m);
        }

        /// <summary>Brings the window back, whatever state it was left in.</summary>
        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitFromTray()
        {
            _exiting = true;
            Close();
        }

        /// <summary>
        /// Closing the window hides it instead of quitting, so the tray indicator survives.
        /// <para>
        /// That is the whole value of the indicator: knowing you are pointed at a live database is
        /// worth nothing if it only shows while the window is open. It is a setting rather than a
        /// rule, because an app that keeps running after you close it surprises people — hence the
        /// one-off balloon the first time, and an explicit Exit in the tray menu.
        /// </para>
        /// </summary>
        private void MainWindow_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Never argue with a shutdown or with Task Manager.
            if (_exiting || _hostsTray is null) return;
            if (e.CloseReason != CloseReason.UserClosing) return;

            // Blocking here is deliberate and safe: FormClosing has to decide e.Cancel before it
            // returns, so it cannot await, and by the time a window can be closed the settings have
            // long been read and cached — GetAsync completes synchronously.
            var settings = _serviceProvider.GetRequiredService<IHostsSettingsService>();
            var current = settings.GetAsync().GetAwaiter().GetResult();
            if (!current.MinimizeToTray) return;

            e.Cancel = true;
            Hide();

            if (current.TrayHintShown) return;

            _hostsTray.ShowHiddenToTrayHint();
            current.TrayHintShown = true;
            _ = settings.SaveAsync(current);
        }

        private async Task StartHealthMonitoringAsync()
        {
            try
            {
                var monitoring = _serviceProvider.GetRequiredService<IHealthMonitoringService>();
                monitoring.ServiceAlertRaised += OnServiceAlertRaised;
                await monitoring.InitializeAsync();
            }
            catch (InvalidOperationException ex)
            {
                // A bad config must not stop the app from opening; the Service Pulse
                // tab surfaces the same failure through ConfigError.
                Debug.WriteLine($"Service Pulse failed to start: {ex.Message}");
            }
        }

        /// <summary>Raised from a monitor loop, so this has to be marshalled — same shape as
        /// <see cref="HostsTrayIcon"/>'s own <c>OnHostsChanged</c>.</summary>
        private void OnServiceAlertRaised(object? sender, ServiceAlertEventArgs e)
        {
            if (IsDisposed) return;

            try
            {
                if (InvokeRequired) BeginInvoke(() => ShowServiceAlertBalloon(e));
                else ShowServiceAlertBalloon(e);
            }
            catch (ObjectDisposedException)
            {
                // The window went away between the check and the call.
            }
            catch (InvalidOperationException)
            {
                // No handle yet; the next alert will catch up.
            }
        }

        /// <summary>
        /// No tray icon exists if the user turned it off in Host Changer settings, or if this
        /// races ahead of <see cref="CreateHostsTrayAsync"/> — either way the alert is simply
        /// not shown rather than falling back to something more intrusive like a MessageBox.
        /// </summary>
        private void ShowServiceAlertBalloon(ServiceAlertEventArgs e)
        {
            if (_hostsTray is null) return;

            if (e.IsRecovery)
            {
                _hostsTray.ShowBalloon($"{e.ServiceName} is back online", "Service Pulse", ToolTipIcon.Info);
            }
            else
            {
                _hostsTray.ShowBalloon($"{e.ServiceName} is down", $"{e.ConsecutiveFailures} consecutive failed pings — Service Pulse", ToolTipIcon.Warning);
            }
        }

        // DPI awareness for Windows
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}