using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.UI.Linux;

/// <summary>
/// linux.yaml: the settings only the Linux host has, in the same config folder as the rest
/// (~/.local/share/DevToolbox/Config).
/// <para>
/// A file of the host's own rather than a switch on the Settings page, because that page is shared:
/// a Linux-only choice there would show on Windows too. Read before any service exists, since it
/// decides which window to open, so it goes to the file directly rather than through
/// IYamlStorageService — with the same camelCase names and the same tolerance of unknown keys.
/// </para>
/// </summary>
internal sealed class LinuxSettings
{
    /// <summary><c>window</c> (the default): DevToolbox's own window. <c>browser</c>: a Chrome app window.</summary>
    public string OpenIn { get; set; } = "window";

    public bool OpensInBrowser => string.Equals(OpenIn?.Trim(), "browser", StringComparison.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox", "Config", "linux.yaml");

    /// <summary>The settings, or the defaults when the file is missing or cannot be read.</summary>
    public static LinuxSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new LinuxSettings();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<LinuxSettings>(File.ReadAllText(FilePath)) ?? new LinuxSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
        {
            // A typo in a settings file must not stop the app; say so and carry on with the defaults.
            Console.Error.WriteLine($"Ignoring {FilePath}: {ex.Message}");
            return new LinuxSettings();
        }
    }
}
