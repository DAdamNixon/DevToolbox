using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using Microsoft.Extensions.DependencyInjection;

namespace DevToolbox.Services;

/// <summary>The Linux and macOS implementations of the platform services, as singletons.</summary>
public static class UnixPlatform
{
    public static IServiceCollection AddUnixPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISystemService, UnixSystemService>();
        services.AddSingleton<IHostsWriteBroker, UnixHostsWriteBroker>();
        services.AddSingleton<IHostsPermissionService, UnixHostsPermissionService>();
        services.AddSingleton<IPathPicker, UnixPathPicker>();
        return services;
    }
}
