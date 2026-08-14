using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <inheritdoc cref="IHealthMonitoringService"/>
    /// <remarks>
    /// One <see cref="PeriodicTimer"/> loop per enabled endpoint, each on its own
    /// task, all cancelled together. This replaced a
    /// <c>ConcurrentDictionary&lt;string, System.Threading.Timer&gt;</c> whose
    /// callbacks were <c>async void</c> lambdas — a slow endpoint could overlap
    /// itself, and nothing could observe or await a shutdown.
    /// </remarks>
    public class HealthMonitoringService : IHealthMonitoringService, IDisposable
    {
        private const string ConfigKey = "service_health_config";

        /// <summary>Ping history kept per service. Bounds memory on a long-running app.</summary>
        private const int MaxHistory = 100;

        private readonly IYamlStorageService _yamlStorage;
        private readonly ConcurrentDictionary<string, ServiceHealth> _health = new();
        private readonly ConcurrentDictionary<string, ServiceEndpoint> _endpoints = new();
        private readonly ConcurrentDictionary<string, Task> _monitors = new();

        // Guards start/stop/initialize against each other. Without it, the page and
        // the app-start call can both decide monitoring is not running and each
        // spawn a full set of loops.
        private readonly SemaphoreSlim _lifecycle = new(1, 1);

        private HttpClient _httpClient;
        private CancellationTokenSource? _monitorCts;
        private ProxySettings? _proxySettings;
        private bool _initialized;
        private bool _disposed;

        public bool IsMonitoring { get; private set; }

        public string? ConfigError { get; private set; }

        public event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged = delegate { };

        public HealthMonitoringService(IYamlStorageService yamlStorage)
        {
            _yamlStorage = yamlStorage;

            // Only enough to have a usable client if someone pings before
            // InitializeAsync. The real one is built once the proxy config is known.
            _httpClient = BuildHttpClient(null);
        }

        // ── lifecycle ────────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized) return;

                await LoadConfigurationCoreAsync().ConfigureAwait(false);

                // Rebuilt here rather than in the constructor because the proxy
                // settings it needs only exist once the config has been read.
                _httpClient.Dispose();
                _httpClient = BuildHttpClient(_proxySettings);

                _initialized = true;
                StartMonitoringCore();
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        public async Task StartMonitoringAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_initialized)
                {
                    await LoadConfigurationCoreAsync().ConfigureAwait(false);
                    _initialized = true;
                }
                StartMonitoringCore();
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        public async Task StopMonitoringAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopMonitoringCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Caller must hold <see cref="_lifecycle"/>.</summary>
        private void StartMonitoringCore()
        {
            if (IsMonitoring) return;

            _monitorCts = new CancellationTokenSource();
            IsMonitoring = true;

            foreach (var endpoint in _endpoints.Values.Where(e => e.IsEnabled))
            {
                StartMonitor(endpoint, _monitorCts.Token);
            }
        }

        /// <summary>Caller must hold <see cref="_lifecycle"/>.</summary>
        private async Task StopMonitoringCoreAsync()
        {
            if (!IsMonitoring) return;

            IsMonitoring = false;
            if (_monitorCts is not null) await _monitorCts.CancelAsync().ConfigureAwait(false);

            // Wait for the loops to actually exit, so a restart cannot double up.
            var running = _monitors.Values.ToArray();
            _monitors.Clear();
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how the loops end.
            }

            _monitorCts?.Dispose();
            _monitorCts = null;
        }

        private void StartMonitor(ServiceEndpoint endpoint, CancellationToken token)
        {
            _monitors[endpoint.Id] = Task.Run(() => MonitorLoopAsync(endpoint, token), token);
        }

        /// <summary>
        /// Pings once immediately, then on the configured interval. The interval is
        /// the gap *between* pings, so a slow endpoint stretches its own schedule
        /// instead of queuing overlapping requests against itself.
        /// </summary>
        private async Task MonitorLoopAsync(ServiceEndpoint endpoint, CancellationToken token)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, endpoint.PingIntervalSeconds));
            using var timer = new PeriodicTimer(interval);

            try
            {
                while (true)
                {
                    await PingAndRecordAsync(endpoint, token).ConfigureAwait(false);
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) return;
                }
            }
            catch (OperationCanceledException)
            {
                // Monitoring stopped, or the app is shutting down.
            }
        }

        // ── pinging ──────────────────────────────────────────────────────────

        public async Task<PingResult> PingServiceAsync(string serviceId)
        {
            if (!_endpoints.TryGetValue(serviceId, out var endpoint))
            {
                return new PingResult
                {
                    Timestamp = DateTime.UtcNow,
                    IsSuccess = false,
                    ErrorMessage = "Service not found",
                    StatusCode = 0
                };
            }

            // Records as well as returns. The manual "Ping Now" button used to throw
            // the result away, so it looked like the button did nothing.
            return await PingAndRecordAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<PingResult> PingAndRecordAsync(ServiceEndpoint endpoint, CancellationToken token)
        {
            var result = await PingEndpointAsync(endpoint, token).ConfigureAwait(false);

            var health = _health.GetOrAdd(endpoint.Id, _ => NewHealth(endpoint));
            lock (health)
            {
                health.ServiceName = endpoint.Name;
                health.LastPing = result.Timestamp;
                health.PingHistory.Add(result);
                if (health.PingHistory.Count > MaxHistory) health.PingHistory.RemoveAt(0);

                health.Status = ClassifyStatus(endpoint, result);
                health.LastError = result.IsSuccess ? null : result.ErrorMessage;
                health.Details = result.Details;

                CalculateMetrics(health, endpoint);
            }

            ServiceHealthChanged.Invoke(this, new ServiceHealthChangedEventArgs(endpoint.Id, health));
            return result;
        }

        private static ServiceStatus ClassifyStatus(ServiceEndpoint endpoint, PingResult result)
        {
            if (!result.IsSuccess) return ServiceStatus.Offline;

            return endpoint.DegradedAboveMs is int limit && result.ResponseTimeMs > limit
                ? ServiceStatus.Degraded
                : ServiceStatus.Online;
        }

        private async Task<PingResult> PingEndpointAsync(ServiceEndpoint endpoint, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new PingResult { Timestamp = DateTime.UtcNow };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                using var response = await _httpClient.GetAsync(endpoint.Endpoint, linked.Token).ConfigureAwait(false);

                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.StatusCode = (int)response.StatusCode;
                result.IsSuccess = IsExpected(endpoint, response);

                if (!result.IsSuccess)
                {
                    result.ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }

                if (endpoint.Details.Count > 0)
                {
                    var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    result.Details = ExtractDetails(endpoint, body);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Monitoring was stopped mid-request. Not a service failure — rethrow
                // so the loop ends instead of recording a bogus outage.
                throw;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = $"Timed out after {endpoint.TimeoutSeconds}s";
                result.StatusCode = 408;
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                result.StatusCode = (int?)ex.StatusCode ?? 0;
            }
            catch (UriFormatException ex)
            {
                stopwatch.Stop();
                result.IsSuccess = false;
                result.ErrorMessage = $"Bad endpoint URL: {ex.Message}";
            }
            catch (InvalidOperationException ex)
            {
                // HttpClient throws this for a relative or otherwise unusable URI.
                stopwatch.Stop();
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static bool IsExpected(ServiceEndpoint endpoint, HttpResponseMessage response) =>
            endpoint.ExpectStatus.Count > 0
                ? endpoint.ExpectStatus.Contains((int)response.StatusCode)
                : response.IsSuccessStatusCode;

        private static List<HealthDetail> ExtractDetails(ServiceEndpoint endpoint, string body)
        {
            var details = new List<HealthDetail>(endpoint.Details.Count);
            foreach (var spec in endpoint.Details)
            {
                // A path that does not resolve is dropped rather than shown blank:
                // the body shape can change between deploys and a card full of empty
                // rows is worse than a shorter card.
                if (JsonPathReader.TryRead(body, spec.Path, out var value))
                {
                    details.Add(new HealthDetail { Label = spec.Label, Value = value });
                }
            }
            return details;
        }

        // ── metrics ──────────────────────────────────────────────────────────

        private static void CalculateMetrics(ServiceHealth health, ServiceEndpoint endpoint)
        {
            var history = health.PingHistory;
            if (history.Count == 0) return;

            var successful = history.Where(p => p.IsSuccess).ToList();
            var failed = history.Count - successful.Count;

            health.Metrics.TotalPings = history.Count;
            health.Metrics.SuccessfulPings = successful.Count;
            health.Metrics.FailedPings = failed;
            health.Metrics.SuccessRate = (double)successful.Count / history.Count * 100;

            if (successful.Count > 0)
            {
                health.Metrics.AverageResponseTime = (int)successful.Average(p => p.ResponseTimeMs);
                health.Metrics.MinResponseTime = successful.Min(p => p.ResponseTimeMs);
                health.Metrics.MaxResponseTime = successful.Max(p => p.ResponseTimeMs);
                health.Metrics.LastSuccessfulPing = successful.Max(p => p.Timestamp);
            }

            if (failed > 0)
            {
                health.Metrics.LastFailedPing = history.Where(p => !p.IsSuccess).Max(p => p.Timestamp);
            }

            // Each sample stands for one interval's worth of time. The old code
            // hardcoded 30 seconds here regardless of what the endpoint was
            // configured with, so a 5-minute interval under-reported uptime tenfold.
            var perSample = TimeSpan.FromSeconds(Math.Max(1, endpoint.PingIntervalSeconds));
            health.Metrics.Uptime = perSample * successful.Count;
            health.Metrics.Downtime = perSample * failed;
        }

        // ── queries ──────────────────────────────────────────────────────────

        public Task<List<ServiceEndpoint>> GetServiceEndpointsAsync() =>
            Task.FromResult(_endpoints.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());

        public Task<List<ServiceHealth>> GetServiceHealthAsync() =>
            Task.FromResult(_health.Values.OrderBy(h => h.ServiceName, StringComparer.OrdinalIgnoreCase).ToList());

        public Task<ServiceHealth?> GetServiceHealthAsync(string serviceId)
        {
            _health.TryGetValue(serviceId, out var health);
            return Task.FromResult(health);
        }

        // ── configuration ────────────────────────────────────────────────────

        public async Task AddServiceEndpointAsync(ServiceEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (string.IsNullOrWhiteSpace(endpoint.Id)) endpoint.Id = Guid.NewGuid().ToString();

            _endpoints[endpoint.Id] = endpoint;
            _health.TryAdd(endpoint.Id, NewHealth(endpoint));

            await SaveConfigurationAsync().ConfigureAwait(false);
            await RestartMonitorAsync(endpoint).ConfigureAwait(false);
        }

        public async Task UpdateServiceEndpointAsync(ServiceEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            _endpoints[endpoint.Id] = endpoint;

            await SaveConfigurationAsync().ConfigureAwait(false);
            await RestartMonitorAsync(endpoint).ConfigureAwait(false);
        }

        public async Task RemoveServiceEndpointAsync(string serviceId)
        {
            _endpoints.TryRemove(serviceId, out _);
            _health.TryRemove(serviceId, out _);
            await SaveConfigurationAsync().ConfigureAwait(false);

            // The loop for a removed endpoint ends on the next full stop/start. It
            // is harmless in the meantime: its results go to a health entry nothing
            // reads. Cancelling one loop individually would mean a token per
            // endpoint, which is not worth it for a config edit.
        }

        /// <summary>
        /// Picks up an interval or enabled change without disturbing the other
        /// services. A no-op when monitoring is stopped.
        /// </summary>
        private async Task RestartMonitorAsync(ServiceEndpoint endpoint)
        {
            if (!IsMonitoring) return;

            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsMonitoring || _monitorCts is null) return;
                if (endpoint.IsEnabled) StartMonitor(endpoint, _monitorCts.Token);
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        public async Task SaveConfigurationAsync()
        {
            // ProxySettings is carried through rather than rebuilt. Constructing a
            // fresh ServiceHealthConfig here silently dropped it on every save,
            // which is why the file on disk ends with a bare "proxySettings:".
            var config = new ServiceHealthConfig
            {
                Services = _endpoints.Values.ToList(),
                ProxySettings = _proxySettings
            };

            await _yamlStorage.SaveAsync(ConfigKey, config).ConfigureAwait(false);
        }

        public async Task LoadConfigurationAsync()
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                await LoadConfigurationCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Caller must hold <see cref="_lifecycle"/>.</summary>
        private async Task LoadConfigurationCoreAsync()
        {
            ServiceHealthConfig? config;
            try
            {
                config = await _yamlStorage.LoadAsync<ServiceHealthConfig>(ConfigKey).ConfigureAwait(false);
                ConfigError = null;
            }
            catch (InvalidOperationException ex)
            {
                // Malformed YAML. Report it and stop — emphatically do not seed.
                // The old code answered a parse error by writing sample services
                // over the file, so a typo destroyed the user's configuration.
                ConfigError = ex.Message;
                return;
            }

            if (config is null)
            {
                // Genuinely absent, i.e. first run. A seed file is worth writing so
                // there is something to edit, but it must not be anyone's real
                // infrastructure — this is a general-purpose tool.
                config = CreateStarterConfig();
                await _yamlStorage.SaveAsync(ConfigKey, config).ConfigureAwait(false);
            }

            _proxySettings = config.ProxySettings;

            _endpoints.Clear();
            foreach (var service in config.Services)
            {
                if (string.IsNullOrWhiteSpace(service.Id)) service.Id = Guid.NewGuid().ToString();
                _endpoints[service.Id] = service;

                // Health is kept across reloads where the service still exists, so
                // editing one endpoint does not blank every sparkline on the page.
                _health.GetOrAdd(service.Id, _ => NewHealth(service));
            }

            foreach (var staleId in _health.Keys.Where(id => !_endpoints.ContainsKey(id)).ToList())
            {
                _health.TryRemove(staleId, out _);
            }
        }

        private static ServiceHealth NewHealth(ServiceEndpoint endpoint) => new()
        {
            ServiceId = endpoint.Id,
            ServiceName = endpoint.Name,
            Status = ServiceStatus.Unknown,
            LastPing = null,
            PingHistory = new List<PingResult>(),
            Metrics = new HealthMetrics()
        };

        /// <summary>
        /// What a brand-new install gets: one disabled, obviously-fake entry that
        /// documents the shape of the file. Every real endpoint is the user's to add.
        /// </summary>
        private static ServiceHealthConfig CreateStarterConfig() => new()
        {
            Services = new List<ServiceEndpoint>
            {
                new()
                {
                    Id = "example",
                    Name = "Example service",
                    Endpoint = "https://example.com/health",
                    Description = "Replace with a real endpoint, then set isEnabled: true.",
                    Environment = "Example",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 10,
                    IsEnabled = false,
                    DegradedAboveMs = 1500,
                    Details = new List<HealthDetailSpec>
                    {
                        new() { Label = "Status", Path = "status" }
                    }
                }
            }
        };

        // ── http ─────────────────────────────────────────────────────────────

        private static HttpClient BuildHttpClient(ProxySettings? proxy)
        {
            var handler = new HttpClientHandler();

            try
            {
                if (proxy is null || proxy.UseSystemProxy)
                {
                    var systemProxy = WebRequest.GetSystemWebProxy();
                    handler.UseProxy = systemProxy is not null;
                    handler.Proxy = systemProxy;
                    handler.UseDefaultCredentials = true;
                    if (handler.Proxy is not null) handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }
                else if (!string.IsNullOrWhiteSpace(proxy.CustomProxyUrl))
                {
                    var webProxy = new WebProxy(proxy.CustomProxyUrl, proxy.BypassProxyForLocal, proxy.BypassList.ToArray());
                    if (!string.IsNullOrWhiteSpace(proxy.ProxyUsername))
                    {
                        webProxy.Credentials = new NetworkCredential(proxy.ProxyUsername, proxy.ProxyPassword);
                    }
                    handler.UseProxy = true;
                    handler.Proxy = webProxy;
                }
                else
                {
                    handler.UseProxy = false;
                }
            }
            catch (PlatformNotSupportedException)
            {
                handler.UseProxy = false;
            }

            var client = new HttpClient(handler)
            {
                // Per-request timeouts come from each endpoint's own token; this is
                // only a backstop against a request that escapes that.
                Timeout = TimeSpan.FromMinutes(2)
            };

            client.DefaultRequestHeaders.Add("User-Agent", "DevToolbox-ServicePulse/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

            return client;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Not StopMonitoringAsync().Wait() — that took the lifecycle semaphore
            // and blocked, which on the UI thread is a deadlock. Cancelling is
            // enough; the loops observe it and unwind on their own.
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            IsMonitoring = false;

            _httpClient.Dispose();
            _lifecycle.Dispose();
        }
    }
}
