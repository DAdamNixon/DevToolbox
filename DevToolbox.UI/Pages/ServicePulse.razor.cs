using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.AspNetCore.Components;

namespace DevToolbox.UI.Pages
{
    public partial class ServicePulse : ComponentBase, IDisposable
    {
        [Inject] private IHealthMonitoringService HealthMonitoring { get; set; } = null!;

        private List<ServiceHealth> serviceHealthList = new();
        private List<ServiceEndpoint> serviceEndpoints = new();

        private bool showAddServiceModal;
        private bool isEditMode;
        private ServiceEndpoint? serviceBeingEdited;

        // Only redraws the relative "3s ago" labels. Actual health updates arrive on
        // the ServiceHealthChanged event; this page no longer polls for them, which
        // is what the old 5-second timer was really doing.
        // Fully qualified: this is a WinForms host, so an unqualified Timer is
        // ambiguous with System.Windows.Forms.Timer, which only ticks on the UI thread.
        private System.Threading.Timer? clockTimer;

        private bool IsMonitoring => HealthMonitoring.IsMonitoring;
        private string? ConfigError => HealthMonitoring.ConfigError;

        private int OnlineServices => serviceHealthList.Count(s => s.Status == ServiceStatus.Online);
        private int OfflineServices => serviceHealthList.Count(s => s.Status == ServiceStatus.Offline);
        private int TotalServices => serviceHealthList.Count;

        private int AverageResponseTime
        {
            get
            {
                var responded = serviceHealthList
                    .Where(s => s.Metrics.AverageResponseTime > 0)
                    .Select(s => s.Metrics.AverageResponseTime)
                    .ToList();
                return responded.Count == 0 ? 0 : (int)responded.Average();
            }
        }

        // Averaged over services that have actually been pinged. Including the ones
        // still at Unknown would drag the figure towards zero at startup and read as
        // an outage.
        private double OverallSuccessRate
        {
            get
            {
                var pinged = serviceHealthList.Where(s => s.Metrics.TotalPings > 0).ToList();
                return pinged.Count == 0 ? 0 : pinged.Average(s => s.Metrics.SuccessRate);
            }
        }

        protected override async Task OnInitializedAsync()
        {
            HealthMonitoring.ServiceHealthChanged += OnServiceHealthChanged;

            // Normally already done at app start; this covers opening the tab before
            // that has finished, and is a no-op once initialised.
            await HealthMonitoring.InitializeAsync();
            await LoadDataAsync();

            clockTimer = new System.Threading.Timer(_ => _ = InvokeAsync(StateHasChanged), null,
                                                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private async Task LoadDataAsync()
        {
            serviceEndpoints = await HealthMonitoring.GetServiceEndpointsAsync();
            serviceHealthList = await HealthMonitoring.GetServiceHealthAsync();
        }

        // Raised from a monitor loop, so this is not on the UI thread. InvokeAsync
        // marshals it; the await inside matters too, because the previous version
        // called StateHasChanged before the reload had actually assigned the lists.
        private void OnServiceHealthChanged(object? sender, ServiceHealthChangedEventArgs e)
        {
            _ = InvokeAsync(async () =>
            {
                await LoadDataAsync();
                StateHasChanged();
            });
        }

        private async Task ToggleMonitoringAsync()
        {
            if (HealthMonitoring.IsMonitoring)
            {
                await HealthMonitoring.StopMonitoringAsync();
            }
            else
            {
                await HealthMonitoring.StartMonitoringAsync();
            }

            await LoadDataAsync();
        }

        private async Task ManualPingAsync(string serviceId)
        {
            await HealthMonitoring.PingServiceAsync(serviceId);
            await LoadDataAsync();
        }

        private void AddNewService()
        {
            isEditMode = false;
            serviceBeingEdited = null;
            showAddServiceModal = true;
        }

        private void EditService(string serviceId)
        {
            serviceBeingEdited = serviceEndpoints.FirstOrDefault(s => s.Id == serviceId);
            if (serviceBeingEdited is null) return;

            isEditMode = true;
            showAddServiceModal = true;
        }

        private async Task HandleServiceSaveAsync(ServiceEndpoint service)
        {
            if (isEditMode)
            {
                await HealthMonitoring.UpdateServiceEndpointAsync(service);
            }
            else
            {
                await HealthMonitoring.AddServiceEndpointAsync(service);
            }

            showAddServiceModal = false;
            serviceBeingEdited = null;
            await LoadDataAsync();
        }

        private void HandleServiceCancel()
        {
            showAddServiceModal = false;
            serviceBeingEdited = null;
        }

        private async Task RemoveServiceAsync(string serviceId)
        {
            await HealthMonitoring.RemoveServiceEndpointAsync(serviceId);
            await LoadDataAsync();
        }

        // ── presentation ─────────────────────────────────────────────────────

        /// <summary>
        /// The complete class strings for one status.
        /// <para>
        /// Spelled out rather than composed as <c>$"bg-{colour}-400"</c>, because
        /// Tailwind scans source text for literal class names and cannot see a
        /// string built at runtime. The interpolated version only ever worked for
        /// colours that happened to appear literally somewhere else — <c>bg-gray-400</c>
        /// did not, so an unmonitored service rendered a colourless dot, which read
        /// as "this page is broken".
        /// </para>
        /// </summary>
        private sealed record StatusStyle(
            string Dot,
            string Chip,
            string Text,
            string Bar,
            string CardBorder,
            string Icon);

        private static StatusStyle StyleFor(ServiceStatus status) => status switch
        {
            ServiceStatus.Online => new StatusStyle(
                "bg-green-400", "bg-green-500/20 text-green-300", "text-green-300",
                "bg-green-500", "border-green-500/30", "bi-check-circle-fill"),

            ServiceStatus.Offline => new StatusStyle(
                "bg-red-400", "bg-red-500/20 text-red-300", "text-red-300",
                "bg-red-500", "border-red-500/30", "bi-x-circle-fill"),

            ServiceStatus.Degraded => new StatusStyle(
                "bg-yellow-400", "bg-yellow-500/20 text-yellow-300", "text-yellow-300",
                "bg-yellow-500", "border-yellow-500/30", "bi-exclamation-triangle-fill"),

            ServiceStatus.Maintenance => new StatusStyle(
                "bg-blue-400", "bg-blue-500/20 text-blue-300", "text-blue-300",
                "bg-blue-500", "border-blue-500/30", "bi-tools"),

            _ => new StatusStyle(
                "bg-gray-400", "bg-gray-500/20 text-gray-300", "text-gray-300",
                "bg-gray-500", "border-gray-500/30", "bi-question-circle-fill"),
        };

        private static string DescribeLastPing(ServiceHealth health)
        {
            if (health.LastPing is not DateTime last) return "not pinged yet";

            var ago = DateTime.UtcNow - last;
            if (ago < TimeSpan.FromSeconds(2)) return "just now";
            if (ago < TimeSpan.FromMinutes(1)) return $"{(int)ago.TotalSeconds}s ago";
            if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes}m ago";
            return $"{(int)ago.TotalHours}h ago";
        }

        private static string DescribePing(PingResult ping) =>
            ping.IsSuccess
                ? $"{ping.Timestamp.ToLocalTime():HH:mm:ss} — {ping.ResponseTimeMs}ms"
                : $"{ping.Timestamp.ToLocalTime():HH:mm:ss} — {ping.ErrorMessage}";

        /// <summary>Where the average sits between the fastest and slowest response.</summary>
        private static double BarWidthPercent(ServiceHealth health)
        {
            var max = Math.Max(1, health.Metrics.MaxResponseTime);
            return Math.Clamp((double)health.Metrics.AverageResponseTime / max * 100, 0, 100);
        }

        public void Dispose()
        {
            clockTimer?.Dispose();
            HealthMonitoring.ServiceHealthChanged -= OnServiceHealthChanged;
        }
    }
}
