using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    public class ServiceHealthConfig
    {
        public List<ServiceEndpoint> Services { get; set; } = new();
        public ProxySettings? ProxySettings { get; set; }
    }

    public class ProxySettings
    {
        public bool UseSystemProxy { get; set; } = true;
        public bool BypassProxyForLocal { get; set; } = true;
        public string? CustomProxyUrl { get; set; }
        public string? ProxyUsername { get; set; }
        public string? ProxyPassword { get; set; }
        public List<string> BypassList { get; set; } = new();
    }

    public class ServiceEndpoint
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public int PingIntervalSeconds { get; set; } = 30;
        public string? Description { get; set; }
        public string? Environment { get; set; }
        public List<string> Tags { get; set; } = new();
        public int TimeoutSeconds { get; set; } = 10;
        public bool IsEnabled { get; set; } = true;
    }

    public class ServiceHealth
    {
        public string ServiceId { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public ServiceStatus Status { get; set; } = ServiceStatus.Unknown;
        public DateTime LastPing { get; set; }
        public List<PingResult> PingHistory { get; set; } = new();
        public HealthMetrics Metrics { get; set; } = new();
    }

    public class PingResult
    {
        public DateTime Timestamp { get; set; }
        public bool IsSuccess { get; set; }
        public int ResponseTimeMs { get; set; }
        public string? ErrorMessage { get; set; }
        public int StatusCode { get; set; }
    }

    public class HealthMetrics
    {
        public double SuccessRate { get; set; }
        public int AverageResponseTime { get; set; }
        public int MinResponseTime { get; set; }
        public int MaxResponseTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public TimeSpan Downtime { get; set; }
        public DateTime? LastSuccessfulPing { get; set; }
        public DateTime? LastFailedPing { get; set; }
        public int TotalPings { get; set; }
        public int SuccessfulPings { get; set; }
        public int FailedPings { get; set; }
    }

    public enum ServiceStatus
    {
        Unknown,
        Online,
        Offline,
        Degraded,
        Maintenance
    }
}