using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using DevToolbox.Services.Services.Hosts;
using Microsoft.Extensions.DependencyInjection;

namespace DevToolbox.Services;

/// <summary>
/// The Windows implementations of the platform services, registered as the singletons they have
/// always been. The folder picker is not among them: it is Windows Forms, so the window host
/// registers its own.
/// </summary>
public static class WindowsPlatform
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISystemService, SystemService>();
        services.AddSingleton<IHostsWriteBroker, HostsWriteBroker>();
        services.AddSingleton<IHostsPermissionService, HostsPermissionService>();
        return services;
    }
}
