using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// Monitor-loop lifecycle. HealthMonitoringService had no tests at all, which is how two
/// duplicate-loop defects survived: editing a service started a SECOND loop for it without
/// stopping the first, and unchecking "enable monitoring" never stopped the running one.
/// Both were invisible until alerts existed - two loops advance ConsecutiveFailures twice per
/// interval, so an alert configured for N failures fires at N/2, and a service switched off
/// carried on raising balloons.
/// <para>
/// Endpoints deliberately point at 127.0.0.1:9 (discard) so a ping fails on connection refused
/// immediately rather than waiting out a timeout, and intervals are long so only the loop's
/// initial ping runs. Nothing here depends on wall-clock timing.
/// </para>
/// </summary>
public class HealthMonitorLifecycleTests
{
    private sealed class FakeYamlStorage : IYamlStorageService
    {
        private readonly Dictionary<string, object?> _store = new();

        public string StorageDirectory => "in-memory";
        public int SaveCount { get; private set; }

        public FakeYamlStorage(ServiceHealthConfig seed) => _store["service_health_config"] = seed;

        public Task SaveAsync<T>(string fileName, T data)
        {
            SaveCount++;
            _store[fileName] = data;
            return Task.CompletedTask;
        }

        public Task<T?> LoadAsync<T>(string fileName) =>
            Task.FromResult(_store.TryGetValue(fileName, out var v) ? (T?)v : default);

        public Task<bool> DeleteAsync(string fileName) => Task.FromResult(_store.Remove(fileName));

        public Task<List<string>> ListFilesAsync() => Task.FromResult(_store.Keys.ToList());
    }

    private static ServiceEndpoint Endpoint(string id, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        // Discard port: refuses instantly, so no test waits on a network timeout.
        Endpoint = "http://127.0.0.1:9/",
        PingIntervalSeconds = 3600,   // only the loop's immediate first ping happens
        TimeoutSeconds = 1,
        IsEnabled = enabled,
    };

    private static (HealthMonitoringService Service, FakeYamlStorage Storage) Build(params ServiceEndpoint[] endpoints)
    {
        var storage = new FakeYamlStorage(new ServiceHealthConfig { Services = endpoints.ToList() });
        return (new HealthMonitoringService(storage), storage);
    }

    [Fact]
    public async Task One_enabled_endpoint_produces_exactly_one_loop()
    {
        var (service, _) = Build(Endpoint("a"));
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(1, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task A_disabled_endpoint_gets_no_loop()
    {
        var (service, _) = Build(Endpoint("a", enabled: false));
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(0, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task Updating_a_service_does_not_leave_a_second_loop_behind()
    {
        // The regression that mattered most: enabling alerts on an existing service REQUIRES an
        // edit, so this path could not be avoided by anyone using the feature.
        var endpoint = Endpoint("a");
        var (service, _) = Build(endpoint);
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(1, service.RunningMonitorCount);

            endpoint.AlertsEnabled = true;
            endpoint.AlertThreshold = 3;
            await service.UpdateServiceEndpointAsync(endpoint);

            Assert.Equal(1, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task Repeated_edits_never_accumulate_loops()
    {
        var endpoint = Endpoint("a");
        var (service, _) = Build(endpoint);
        using (service)
        {
            await service.InitializeAsync();

            for (var i = 0; i < 5; i++)
            {
                endpoint.Description = $"edit {i}";
                await service.UpdateServiceEndpointAsync(endpoint);
                Assert.Equal(1, service.RunningMonitorCount);
            }
        }
    }

    [Fact]
    public async Task Disabling_a_service_actually_stops_its_loop()
    {
        // "The off switch does nothing" - the loop kept pinging and kept raising alerts for a
        // service the user had just switched off.
        var endpoint = Endpoint("a");
        var (service, _) = Build(endpoint);
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(1, service.RunningMonitorCount);

            endpoint.IsEnabled = false;
            await service.UpdateServiceEndpointAsync(endpoint);

            Assert.Equal(0, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task Re_enabling_a_service_starts_exactly_one_loop_again()
    {
        var endpoint = Endpoint("a");
        var (service, _) = Build(endpoint);
        using (service)
        {
            await service.InitializeAsync();

            endpoint.IsEnabled = false;
            await service.UpdateServiceEndpointAsync(endpoint);
            Assert.Equal(0, service.RunningMonitorCount);

            endpoint.IsEnabled = true;
            await service.UpdateServiceEndpointAsync(endpoint);
            Assert.Equal(1, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task Removing_a_service_stops_its_loop()
    {
        var (service, _) = Build(Endpoint("a"), Endpoint("b"));
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(2, service.RunningMonitorCount);

            await service.RemoveServiceEndpointAsync("a");

            Assert.Equal(1, service.RunningMonitorCount);
        }
    }

    [Fact]
    public async Task Stopping_monitoring_ends_every_loop()
    {
        var (service, _) = Build(Endpoint("a"), Endpoint("b"), Endpoint("c"));
        using (service)
        {
            await service.InitializeAsync();
            Assert.Equal(3, service.RunningMonitorCount);

            await service.StopMonitoringAsync();

            Assert.Equal(0, service.RunningMonitorCount);
            Assert.False(service.IsMonitoring);
        }
    }

    [Fact]
    public async Task Stop_then_start_does_not_double_up()
    {
        var (service, _) = Build(Endpoint("a"), Endpoint("b"));
        using (service)
        {
            await service.InitializeAsync();
            await service.StopMonitoringAsync();
            await service.StartMonitoringAsync();

            Assert.Equal(2, service.RunningMonitorCount);
        }
    }
}
