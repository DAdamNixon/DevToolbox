using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    public interface IHealthMonitoringService
    {
        /// <summary>
        /// Gets all configured service endpoints
        /// </summary>
        Task<List<ServiceEndpoint>> GetServiceEndpointsAsync();

        /// <summary>
        /// Gets health status for all services
        /// </summary>
        Task<List<ServiceHealth>> GetServiceHealthAsync();

        /// <summary>
        /// Gets health status for a specific service
        /// </summary>
        Task<ServiceHealth?> GetServiceHealthAsync(string serviceId);

        /// <summary>
        /// Starts monitoring all enabled services
        /// </summary>
        Task StartMonitoringAsync();

        /// <summary>
        /// Stops all monitoring
        /// </summary>
        Task StopMonitoringAsync();

        /// <summary>
        /// Manually ping a specific service
        /// </summary>
        Task<PingResult> PingServiceAsync(string serviceId);

        /// <summary>
        /// Add a new service endpoint
        /// </summary>
        Task AddServiceEndpointAsync(ServiceEndpoint endpoint);

        /// <summary>
        /// Update an existing service endpoint
        /// </summary>
        Task UpdateServiceEndpointAsync(ServiceEndpoint endpoint);

        /// <summary>
        /// Remove a service endpoint
        /// </summary>
        Task RemoveServiceEndpointAsync(string serviceId);

        /// <summary>
        /// Save service configuration to YAML
        /// </summary>
        Task SaveConfigurationAsync();

        /// <summary>
        /// Load service configuration from YAML
        /// </summary>
        Task LoadConfigurationAsync();

        /// <summary>
        /// Event raised when service health changes
        /// </summary>
        event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged;
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
}