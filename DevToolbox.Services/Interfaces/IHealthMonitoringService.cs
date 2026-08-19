using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    /// <summary>
    /// Polls the configured endpoints on background loops and reports what it finds.
    /// Registered as a singleton and started once at app launch, so monitoring runs
    /// whether or not the Service Pulse tab is open.
    /// </summary>
    public interface IHealthMonitoringService
    {
        /// <summary>
        /// Loads configuration and starts the monitor loops. Safe to call more than
        /// once; only the first call does anything.
        /// <para>
        /// Awaiting this matters: the old code kicked the load off from the
        /// constructor with a bare <c>Task.Run</c>, so a page that rendered first
        /// saw no services at all and showed the "nothing configured" empty state.
        /// </para>
        /// </summary>
        Task InitializeAsync();

        /// <summary>All configured endpoints, enabled or not.</summary>
        Task<List<ServiceEndpoint>> GetServiceEndpointsAsync();

        /// <summary>Current health of every configured service.</summary>
        Task<List<ServiceHealth>> GetServiceHealthAsync();

        /// <summary>Current health of one service, or null if it is not configured.</summary>
        Task<ServiceHealth?> GetServiceHealthAsync(string serviceId);

        /// <summary>Whether the monitor loops are currently running.</summary>
        bool IsMonitoring { get; }

        /// <summary>
        /// Why the configuration could not be read, or null. Non-null means the file
        /// exists but is malformed, and it has deliberately been left untouched.
        /// </summary>
        string? ConfigError { get; }

        Task StartMonitoringAsync();

        Task StopMonitoringAsync();

        /// <summary>
        /// Pings one service now and records the result, exactly as a scheduled ping
        /// would. Returns the result for convenience; callers may ignore it and read
        /// the updated <see cref="ServiceHealth"/> instead.
        /// </summary>
        Task<PingResult> PingServiceAsync(string serviceId);

        Task AddServiceEndpointAsync(ServiceEndpoint endpoint);

        Task UpdateServiceEndpointAsync(ServiceEndpoint endpoint);

        Task RemoveServiceEndpointAsync(string serviceId);

        Task SaveConfigurationAsync();

        Task LoadConfigurationAsync();

        /// <summary>
        /// Raised after every ping. Fires on a background thread — Blazor subscribers
        /// must marshal with <c>InvokeAsync</c> before touching component state.
        /// </summary>
        event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged;

        /// <summary>
        /// Raised when a service's alert state changes — armed (down) or cleared
        /// (recovered) — for an endpoint with <see cref="ServiceEndpoint.AlertsEnabled"/> set.
        /// Same threading rules as <see cref="ServiceHealthChanged"/>: fires on a background
        /// thread, and UI subscribers must marshal it.
        /// </summary>
        event EventHandler<ServiceAlertEventArgs> ServiceAlertRaised;
    }

    public class ServiceHealthChangedEventArgs : EventArgs
    {
        public string ServiceId { get; set; } = string.Empty;
        public ServiceHealth ServiceHealth { get; set; } = new();

        public ServiceHealthChangedEventArgs(string serviceId, ServiceHealth serviceHealth)
        {
            ServiceId = serviceId;
            ServiceHealth = serviceHealth;
        }
    }

    public class ServiceAlertEventArgs : EventArgs
    {
        public string ServiceId { get; }
        public string ServiceName { get; }

        /// <summary>False for a down alert, true for the matching recovery.</summary>
        public bool IsRecovery { get; }

        /// <summary>Meaningful only when <see cref="IsRecovery"/> is false.</summary>
        public int ConsecutiveFailures { get; }

        public ServiceAlertEventArgs(string serviceId, string serviceName, bool isRecovery, int consecutiveFailures)
        {
            ServiceId = serviceId;
            ServiceName = serviceName;
            IsRecovery = isRecovery;
            ConsecutiveFailures = consecutiveFailures;
        }
    }
}
