using System;
using System.Collections.Generic;

namespace DevToolbox.Services.Models
{
    /// <summary>Root of Config/service_health_config.yaml.</summary>
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

    /// <summary>
    /// One thing to watch. Everything past <see cref="IsEnabled"/> is optional —
    /// leave it all out and you get a plain "did this URL answer with a 2xx"
    /// check, which is what the tab did before those options existed.
    /// </summary>
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

        /// <summary>
        /// Status codes that count as healthy. Empty means "any 2xx", which is the
        /// right default but the wrong answer for an endpoint that legitimately
        /// answers 401 or 302 when it is up.
        /// </summary>
        public List<int> ExpectStatus { get; set; } = new();

        /// <summary>
        /// Answering, but slower than this, reports <see cref="ServiceStatus.Degraded"/>
        /// instead of <see cref="ServiceStatus.Online"/>. Null disables the distinction.
        /// </summary>
        public int? DegradedAboveMs { get; set; }

        /// <summary>
        /// Fields to pull out of a JSON response body and show on the card. Purely
        /// presentational — a path that does not resolve is dropped, and none of
        /// this affects whether the service counts as up.
        /// <para>
        /// This is how an endpoint that returns more than "I am alive" gets to say
        /// so without the shape of anyone's health payload being compiled in.
        /// </para>
        /// </summary>
        public List<HealthDetailSpec> Details { get; set; } = new();
    }

    /// <summary>Reads one labelled value out of a JSON response body.</summary>
    public class HealthDetailSpec
    {
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Dotted path into the body, e.g. <c>blue.status</c> or <c>nodes[0].name</c>.
        /// </summary>
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>A resolved <see cref="HealthDetailSpec"/> — what actually gets rendered.</summary>
    public class HealthDetail
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class ServiceHealth
    {
        public string ServiceId { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public ServiceStatus Status { get; set; } = ServiceStatus.Unknown;

        /// <summary>
        /// When this service was last actually pinged, or null if it has not been
        /// yet. Nullable because the old code stamped it with "now" at load and the
        /// UI then read that back as proof that monitoring was running.
        /// </summary>
        public DateTime? LastPing { get; set; }

        public List<PingResult> PingHistory { get; set; } = new();
        public HealthMetrics Metrics { get; set; } = new();

        /// <summary>Why the service is down, when it is. Null while healthy.</summary>
        public string? LastError { get; set; }

        /// <summary>Values pulled from the most recent response body; empty if none were configured.</summary>
        public List<HealthDetail> Details { get; set; } = new();
    }

    public class PingResult
    {
        public DateTime Timestamp { get; set; }
        public bool IsSuccess { get; set; }
        public int ResponseTimeMs { get; set; }
        public string? ErrorMessage { get; set; }
        public int StatusCode { get; set; }
        public List<HealthDetail> Details { get; set; } = new();
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
