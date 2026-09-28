using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;

namespace DevToolbox.Cli;

/// <summary>The platform's own implementations, chosen when the command runs.</summary>
internal static class Platform
{
    public static ISystemService SystemService(PowerShellService powerShell) =>
        OperatingSystem.IsWindows()
            ? new DevToolbox.Services.Services.SystemService(powerShell)
            : new UnixSystemService(powerShell);
}
