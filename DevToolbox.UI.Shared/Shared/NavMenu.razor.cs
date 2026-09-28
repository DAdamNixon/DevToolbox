using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace DevToolbox.UI.Shared
{
    public partial class NavMenu : ComponentBase, IDisposable
    {
        [Inject] private NavigationManager NavigationManager { get; set; } = null!;
        [Inject] private IHealthMonitoringService HealthMonitoring { get; set; } = null!;

        /// <summary>
        /// Complete literal class strings, exactly like <c>ServicePulse.razor.cs</c>'s
        /// StatusStyle — Tailwind only emits classes it can see as text, so a name built
        /// with string interpolation compiles but never renders (see the DevToolbox
        /// Decisions.md entry "Tailwind cannot see interpolated class names").
        /// </summary>
        private sealed record HealthPill(string Dot, string TextClass, string Label);

        private static readonly HealthPill NotMonitoring = new("status-offline", "text-dark-text-muted", "Not Monitoring");

        private HealthPill pill = NotMonitoring;

        /// <summary>Every tab, defined once and rendered by both the strip and the drawer.</summary>
        private static readonly (string Route, string Icon, string Label)[] Links =
        {
            ("/", "bi-house", "Dashboard"),
            ("/logs", "bi-journal-text", "Log Viewer"),
            ("/powershell", "bi-terminal", "PowerShell"),
            ("/service-pulse", "bi-heart-pulse", "Service Pulse"),
            ("/host-changer", "bi-hdd-network", "Host Changer"),
            ("/settings", "bi-gear", "Settings"),
        };

        /// <summary>The narrow-width drawer. Closed on every navigation, so a tap
        /// on a tab never leaves it hanging over the new page.</summary>
        private bool _drawerOpen;

        private void ToggleDrawer() => _drawerOpen = !_drawerOpen;

        private void CloseDrawer() => _drawerOpen = false;

        protected override async Task OnInitializedAsync()
        {
            NavigationManager.LocationChanged += LocationChanged;
            HealthMonitoring.ServiceHealthChanged += OnServiceHealthChanged;

            // Normally already running by the time any page loads (started from
            // MainWindow), but this is cheap insurance and a no-op once initialised.
            await HealthMonitoring.InitializeAsync();
            await RefreshPillAsync();
        }

        private void LocationChanged(object? sender, LocationChangedEventArgs e)
        {
            _drawerOpen = false;
            StateHasChanged();
        }

        // Raised from a monitor loop, not the UI thread — same reason ServicePulse marshals
        // this with InvokeAsync before touching component state.
        private void OnServiceHealthChanged(object? sender, ServiceHealthChangedEventArgs e) =>
            _ = InvokeAsync(async () =>
            {
                await RefreshPillAsync();
                StateHasChanged();
            });

        private async Task RefreshPillAsync()
        {
            var health = await HealthMonitoring.GetServiceHealthAsync();
            pill = Classify(health);
        }

        /// <summary>
        /// One glance at everything Service Pulse watches, visible from any page — the tab
        /// itself still has the detail. Nothing configured means nothing pretends to be
        /// "online": this is what replaced the old hardcoded green dot.
        /// </summary>
        private static HealthPill Classify(List<ServiceHealth> health)
        {
            if (health.Count == 0)
                return NotMonitoring;

            var down = health.Count(s => s.Status == ServiceStatus.Offline);
            if (down > 0)
                return new HealthPill("status-down", "text-danger", down == 1 ? "1 Down" : $"{down} Down");

            if (health.Any(s => s.Status == ServiceStatus.Degraded))
                return new HealthPill("status-degraded", "text-warning", "Degraded");

            return new HealthPill("status-online", "text-dark-text-muted", "All Online");
        }

        private string GetActiveClass(string route)
        {
            var currentPath = "/" + NavigationManager.ToBaseRelativePath(NavigationManager.Uri).TrimEnd('/');

            // Special case for root
            if (currentPath == "/" && route == "/")
                return "active";

            // For exact matches
            if (currentPath == route)
                return "active";

            // Not active
            return "";
        }

        public void Dispose()
        {
            NavigationManager.LocationChanged -= LocationChanged;
            HealthMonitoring.ServiceHealthChanged -= OnServiceHealthChanged;
        }
    }
}
