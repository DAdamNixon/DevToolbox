using System.Diagnostics;
using System.Reflection;

namespace DevToolbox.Services;

/// <summary>
/// What version of DevToolbox this is, in the two forms anything needs it.
/// <para>
/// Both numbers come from <c>Directory.Build.props</c> by way of the compiler, so nothing here has
/// a version literal in it. The parsing is separated from the reading — <see cref="Describe"/> is a
/// pure function over two strings — because the interesting behaviour is entirely in the parsing
/// and an assembly's own attributes are not something a test can vary.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>Which audience a build was cut for. Derived from the prerelease suffix, not declared.</summary>
    public enum Channel
    {
        /// <summary>No suffix: a release build.</summary>
        Release,
        Beta,
        Alpha,
    }

    /// <param name="Display">For a person: <c>0.4.0-alpha.1</c>.</param>
    /// <param name="Package">Four numeric parts, for MSIX and App Installer: <c>0.4.0.0</c>.</param>
    /// <param name="Channel">Which audience this build was cut for.</param>
    public sealed record Description(string Display, string Package, Channel Channel)
    {
        /// <summary>"alpha", "beta" or null — null being a release, which needs no label in the UI.</summary>
        public string? ChannelLabel => Channel switch
        {
            AppVersion.Channel.Alpha => "alpha",
            AppVersion.Channel.Beta => "beta",
            _ => null,
        };
    }

    private static readonly Description Current = DescribeEntryAssembly();

    /// <summary>For a person: <c>0.4.0-alpha.1</c>.</summary>
    public static string Display => Current.Display;

    /// <summary>Four numeric parts, as MSIX and App Installer compare them: <c>0.4.0.0</c>.</summary>
    public static string Package => Current.Package;

    /// <summary>Which audience this build was cut for.</summary>
    public static Channel ReleaseChannel => Current.Channel;

    /// <summary>"alpha", "beta", or null for a release build.</summary>
    public static string? ChannelLabel => Current.ChannelLabel;

    /// <param name="informationalVersion">
    /// <c>AssemblyInformationalVersionAttribute</c>. May carry a build-metadata suffix after a
    /// <c>+</c> — the .NET SDK appends the commit id there when one is available — which is not
    /// part of what a person wants to read.
    /// </param>
    /// <param name="fileVersion">
    /// <c>AssemblyFileVersionAttribute</c>, which the SDK always fills with four numeric parts.
    /// </param>
    public static Description Describe(string? informationalVersion, string? fileVersion)
    {
        var package = NormalizeToFourParts(fileVersion);

        var display = informationalVersion;
        if (!string.IsNullOrWhiteSpace(display))
        {
            var metadata = display.IndexOf('+');
            if (metadata >= 0) display = display[..metadata];
            display = display.Trim();
        }

        // Falling back to the four-part number rather than to "unknown": a build with no
        // informational version is odd but not broken, and showing 0.4.0.0 is honest.
        if (string.IsNullOrWhiteSpace(display)) display = package;

        return new Description(display, package, ChannelFrom(display));
    }

    private static Channel ChannelFrom(string display)
    {
        var separator = display.IndexOf('-');
        if (separator < 0) return Channel.Release;

        var suffix = display[(separator + 1)..];
        if (suffix.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return Channel.Alpha;
        if (suffix.Contains("beta", StringComparison.OrdinalIgnoreCase)) return Channel.Beta;

        // Some other suffix — rc, or a hand-set one. Not a release, and not a channel we ship to;
        // treated as the most cautious of the two so nothing reads as more finished than it is.
        return Channel.Alpha;
    }

    /// <summary>
    /// Pads or trims to exactly four numeric parts. MSIX accepts nothing else, so the value handed
    /// to the packaging script has to be valid whatever the assembly happened to carry.
    /// </summary>
    private static string NormalizeToFourParts(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return "0.0.0.0";

        var parts = fileVersion.Trim().Split('.');
        var normalized = new string[4];

        for (var i = 0; i < 4; i++)
        {
            var part = i < parts.Length ? parts[i] : "0";
            normalized[i] = int.TryParse(part, out var number) && number >= 0
                ? number.ToString()
                : "0";
        }

        return string.Join('.', normalized);
    }

    private static Description DescribeEntryAssembly()
    {
        // The entry assembly, not this one: the version lives on DevToolbox.UI, and reading the
        // executing assembly would report DevToolbox.Services' own. Null under a test host, where
        // there is no meaningful application version to report.
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return Describe(null, null);

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;

        // A single-file or trimmed publish can leave the attributes off. The executable's own
        // resource is written by the same SDK properties, so it is the same number.
        if (string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(Environment.ProcessPath);
                file = info.FileVersion;
                informational ??= info.ProductVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Showing the wrong version is a cosmetic problem; failing to start is not.
            }
        }

        return Describe(informational, file);
    }
}
