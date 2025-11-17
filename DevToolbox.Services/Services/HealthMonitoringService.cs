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
    public class HealthMonitoringService : IHealthMonitoringService, IDisposable
    {
        private readonly IYamlStorageService _yamlStorage;
        private HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, ServiceHealth> _serviceHealthCache = new();
        private readonly ConcurrentDictionary<string, Timer> _serviceTimers = new();
        private readonly ConcurrentDictionary<string, ServiceEndpoint> _serviceEndpoints = new();
        private readonly string _configKey = "service_health_config";
        private bool _isMonitoring = false;

        public event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged = delegate { };

        public HealthMonitoringService(IYamlStorageService yamlStorage)
        {
            _yamlStorage = yamlStorage;
            
            // Initialize with basic HttpClient first, will reconfigure after loading settings
            ConfigureHttpClient();
            
            // Load configuration on startup
            _ = Task.Run(async () => {
                await LoadConfigurationAsync();
                // Reconfigure HttpClient with loaded proxy settings
                ConfigureHttpClient();
            });
        }

        private void ConfigureHttpClient()
        {
            // Dispose existing client if any
            _httpClient?.Dispose();

            var handler = new HttpClientHandler();
            
            try
            {
                // Try to configure proxy settings
                var systemProxy = WebRequest.GetSystemWebProxy();
                if (systemProxy != null)
                {
                    handler.UseProxy = true;
                    handler.Proxy = systemProxy;
                    handler.UseDefaultCredentials = true;
                    
                    if (handler.Proxy != null)
                    {
                        handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
                    }
                }
                else
                {
                    // No system proxy, disable proxy
                    handler.UseProxy = false;
                }
            }
            catch (Exception)
            {
                // If proxy configuration fails, try without proxy
                handler.UseProxy = false;
            }

            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            // Add headers to make requests more legitimate
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 DevToolbox-ServicePulse/1.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", 
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        }

        public async Task<List<ServiceEndpoint>> GetServiceEndpointsAsync()
        {
            return _serviceEndpoints.Values.ToList();
        }

        public async Task<List<ServiceHealth>> GetServiceHealthAsync()
        {
            return _serviceHealthCache.Values.ToList();
        }

        public async Task<ServiceHealth?> GetServiceHealthAsync(string serviceId)
        {
            _serviceHealthCache.TryGetValue(serviceId, out var health);
            return health;
        }

        public async Task StartMonitoringAsync()
        {
            if (_isMonitoring) return;

            _isMonitoring = true;

            foreach (var endpoint in _serviceEndpoints.Values.Where(e => e.IsEnabled))
            {
                StartMonitoringService(endpoint);
            }
        }

        public async Task StopMonitoringAsync()
        {
            if (!_isMonitoring) return;

            _isMonitoring = false;

            foreach (var timer in _serviceTimers.Values)
            {
                timer?.Dispose();
            }
            _serviceTimers.Clear();
        }

        public async Task<PingResult> PingServiceAsync(string serviceId)
        {
            if (!_serviceEndpoints.TryGetValue(serviceId, out var endpoint))
            {
                return new PingResult
                {
                    Timestamp = DateTime.UtcNow,
                    IsSuccess = false,
                    ErrorMessage = "Service not found",
                    StatusCode = 0
                };
            }

            return await PingEndpointAsync(endpoint);
        }

        public async Task AddServiceEndpointAsync(ServiceEndpoint endpoint)
        {
            if (string.IsNullOrEmpty(endpoint.Id))
            {
                endpoint.Id = Guid.NewGuid().ToString();
            }

            _serviceEndpoints[endpoint.Id] = endpoint;
            
            // Initialize health tracking
            if (!_serviceHealthCache.ContainsKey(endpoint.Id))
            {
                _serviceHealthCache[endpoint.Id] = new ServiceHealth
                {
                    ServiceId = endpoint.Id,
                    ServiceName = endpoint.Name,
                    Status = ServiceStatus.Unknown,
                    LastPing = DateTime.UtcNow,
                    PingHistory = new List<PingResult>(),
                    Metrics = new HealthMetrics()
                };
            }

            if (_isMonitoring && endpoint.IsEnabled)
            {
                StartMonitoringService(endpoint);
            }

            await SaveConfigurationAsync();
        }

        public async Task UpdateServiceEndpointAsync(ServiceEndpoint endpoint)
        {
            _serviceEndpoints[endpoint.Id] = endpoint;

            // Stop existing monitoring
            if (_serviceTimers.TryRemove(endpoint.Id, out var existingTimer))
            {
                existingTimer?.Dispose();
            }

            // Restart monitoring if enabled
            if (_isMonitoring && endpoint.IsEnabled)
            {
                StartMonitoringService(endpoint);
            }

            await SaveConfigurationAsync();
        }

        public async Task RemoveServiceEndpointAsync(string serviceId)
        {
            _serviceEndpoints.TryRemove(serviceId, out _);
            _serviceHealthCache.TryRemove(serviceId, out _);
            
            if (_serviceTimers.TryRemove(serviceId, out var timer))
            {
                timer?.Dispose();
            }

            await SaveConfigurationAsync();
        }

        public async Task SaveConfigurationAsync()
        {
            var config = new ServiceHealthConfig
            {
                Services = _serviceEndpoints.Values.ToList()
            };

            await _yamlStorage.SaveAsync(_configKey, config);
        }

        public async Task LoadConfigurationAsync()
        {
            try
            {
                var config = await _yamlStorage.LoadAsync<ServiceHealthConfig>(_configKey);
                
                if (config?.Services != null && config.Services.Any())
                {
                    _serviceEndpoints.Clear();
                    _serviceHealthCache.Clear();

                    foreach (var service in config.Services)
                    {
                        _serviceEndpoints[service.Id] = service;
                        
                        _serviceHealthCache[service.Id] = new ServiceHealth
                        {
                            ServiceId = service.Id,
                            ServiceName = service.Name,
                            Status = ServiceStatus.Unknown,
                            LastPing = DateTime.UtcNow,
                            PingHistory = new List<PingResult>(),
                            Metrics = new HealthMetrics()
                        };
                    }

                    // Debug: Successfully loaded configuration
                }
                else
                {
                    // Create default sample configuration if none exists
                    await CreateSampleConfiguration();
                }
            }
            catch (Exception ex)
            {
                // Create sample config on error too
                await CreateSampleConfiguration();
            }
        }

        private async Task CreateSampleConfiguration()
        {
            var sampleServices = new List<ServiceEndpoint>
            {
                new ServiceEndpoint
                {
                    Id = "elliott-web02",
                    Name = "Elliott Electric Web02",
                    Endpoint = "http://200.0.2.22/P",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 10,
                    Description = "Elliott Electric Web02 server health check",
                    Environment = "Production",
                    Tags = new List<string> { "internal", "elliott", "web", "web02" },
                    IsEnabled = true
                },
                new ServiceEndpoint
                {
                    Id = "elliott-web02-root",
                    Name = "Elliott Electric Web02 (Root)",
                    Endpoint = "http://200.0.2.22/",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 10,
                    Description = "Elliott Electric Web02 root path",
                    Environment = "Production", 
                    Tags = new List<string> { "internal", "elliott", "web", "web02", "test" },
                    IsEnabled = true
                },
                new ServiceEndpoint
                {
                    Id = "elliott-web03",
                    Name = "Elliott Electric Web03",
                    Endpoint = "http://10.135.0.161/P",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 10,
                    Description = "Elliott Electric Web03 server health check",
                    Environment = "Production",
                    Tags = new List<string> { "internal", "elliott", "web", "web03" },
                    IsEnabled = true
                },
                new ServiceEndpoint
                {
                    Id = "elliott-web04",
                    Name = "Elliott Electric Web04",
                    Endpoint = "http://10.135.0.171/P",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 10,
                    Description = "Elliott Electric Web04 server health check",
                    Environment = "Production",
                    Tags = new List<string> { "internal", "elliott", "web", "web04" },
                    IsEnabled = true
                },
                new ServiceEndpoint
                {
                    Id = "google-dns",
                    Name = "Google DNS (8.8.8.8)",
                    Endpoint = "https://dns.google",
                    PingIntervalSeconds = 60,
                    TimeoutSeconds = 10,
                    Description = "Google's public DNS service - test external connectivity",
                    Environment = "Production",
                    Tags = new List<string> { "dns", "google", "infrastructure", "external" },
                    IsEnabled = true
                },
                new ServiceEndpoint
                {
                    Id = "localhost-test",
                    Name = "Localhost Test",
                    Endpoint = "http://localhost",
                    PingIntervalSeconds = 30,
                    TimeoutSeconds = 5,
                    Description = "Local machine connectivity test",
                    Environment = "Development",
                    Tags = new List<string> { "local", "test" },
                    IsEnabled = true
                }
            };

            foreach (var service in sampleServices)
            {
                _serviceEndpoints[service.Id] = service;
                _serviceHealthCache[service.Id] = new ServiceHealth
                {
                    ServiceId = service.Id,
                    ServiceName = service.Name,
                    Status = ServiceStatus.Unknown,
                    LastPing = DateTime.UtcNow,
                    PingHistory = new List<PingResult>(),
                    Metrics = new HealthMetrics()
                };
            }

            var config = new ServiceHealthConfig { Services = sampleServices };
            await _yamlStorage.SaveAsync(_configKey, config);
        }

        private void StartMonitoringService(ServiceEndpoint endpoint)
        {
            // Stop existing timer if any
            if (_serviceTimers.TryRemove(endpoint.Id, out var existingTimer))
            {
                existingTimer?.Dispose();
            }

            var timer = new Timer(async _ => await MonitorService(endpoint), 
                                 null, 
                                 TimeSpan.Zero, 
                                 TimeSpan.FromSeconds(endpoint.PingIntervalSeconds));
            
            _serviceTimers[endpoint.Id] = timer;
        }

        private async Task MonitorService(ServiceEndpoint endpoint)
        {
            try
            {
                var result = await PingEndpointAsync(endpoint);
                
                if (_serviceHealthCache.TryGetValue(endpoint.Id, out var health))
                {
                    health.LastPing = result.Timestamp;
                    health.PingHistory.Add(result);
                    
                    // Keep only last 100 results to prevent memory bloat
                    if (health.PingHistory.Count > 100)
                    {
                        health.PingHistory.RemoveAt(0);
                    }

                    // Update status based on result
                    health.Status = result.IsSuccess ? ServiceStatus.Online : ServiceStatus.Offline;
                    
                    // Calculate metrics
                    CalculateMetrics(health);

                    // Raise event
                    ServiceHealthChanged.Invoke(this, new ServiceHealthChangedEventArgs(endpoint.Id, health));
                }
            }
            catch (Exception ex)
            {
                // Error monitoring service - could log this in the future
            }
        }

        private async Task<PingResult> PingEndpointAsync(ServiceEndpoint endpoint)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new PingResult
            {
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Try primary method with configured HttpClient
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
                using var response = await _httpClient.GetAsync(endpoint.Endpoint, cts.Token);
                
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.StatusCode = (int)response.StatusCode;
                result.IsSuccess = response.IsSuccessStatusCode;
                
                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}";
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("proxy") || ex.Message.Contains("Proxy"))
            {
                stopwatch.Stop();
                // Try alternative method without proxy
                result = await TryAlternativePingAsync(endpoint);
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = $"Timeout after {endpoint.TimeoutSeconds} seconds";
                result.StatusCode = 408; // Request Timeout
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                result.StatusCode = 0;
                
                // If the error seems proxy-related, try alternative method
                if (ex.Message.Contains("proxy") || ex.Message.Contains("Proxy") || 
                    ex.Message.Contains("407") || ex.Message.Contains("authentication"))
                {
                    result = await TryAlternativePingAsync(endpoint);
                }
            }

            return result;
        }

        private async Task<PingResult> TryAlternativePingAsync(ServiceEndpoint endpoint)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new PingResult
            {
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Create a new HttpClient without proxy for this request
                using var handler = new HttpClientHandler()
                {
                    UseProxy = false
                };
                
                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);
                
                // Add basic headers
                client.DefaultRequestHeaders.Add("User-Agent", "DevToolbox-ServicePulse/1.0");
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
                using var response = await client.GetAsync(endpoint.Endpoint, cts.Token);
                
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.StatusCode = (int)response.StatusCode;
                result.IsSuccess = response.IsSuccessStatusCode;
                
                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase} (via direct connection)";
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.ErrorMessage = $"Direct connection failed: {ex.Message}";
                result.StatusCode = 0;
            }

            return result;
        }

        private void CalculateMetrics(ServiceHealth health)
        {
            if (!health.PingHistory.Any()) return;

            var successfulPings = health.PingHistory.Where(p => p.IsSuccess).ToList();
            var failedPings = health.PingHistory.Where(p => !p.IsSuccess).ToList();

            health.Metrics.TotalPings = health.PingHistory.Count;
            health.Metrics.SuccessfulPings = successfulPings.Count;
            health.Metrics.FailedPings = failedPings.Count;
            health.Metrics.SuccessRate = (double)successfulPings.Count / health.PingHistory.Count * 100;

            if (successfulPings.Any())
            {
                health.Metrics.AverageResponseTime = (int)successfulPings.Average(p => p.ResponseTimeMs);
                health.Metrics.MinResponseTime = successfulPings.Min(p => p.ResponseTimeMs);
                health.Metrics.MaxResponseTime = successfulPings.Max(p => p.ResponseTimeMs);
                health.Metrics.LastSuccessfulPing = successfulPings.Max(p => p.Timestamp);
            }

            if (failedPings.Any())
            {
                health.Metrics.LastFailedPing = failedPings.Max(p => p.Timestamp);
            }

            // Calculate uptime/downtime (simplified - based on recent history)
            var recentPings = health.PingHistory.TakeLast(20).ToList();
            var uptimeCount = recentPings.Count(p => p.IsSuccess);
            var totalTime = recentPings.Count * 30; // Assuming 30 second intervals
            
            health.Metrics.Uptime = TimeSpan.FromSeconds(uptimeCount * 30);
            health.Metrics.Downtime = TimeSpan.FromSeconds((recentPings.Count - uptimeCount) * 30);
        }

        public void Dispose()
        {
            StopMonitoringAsync().Wait();
            _httpClient?.Dispose();
        }
    }
}