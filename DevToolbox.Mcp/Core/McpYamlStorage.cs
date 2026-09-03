using DevToolbox.Services.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// This server's own <see cref="IYamlStorageService"/>, reading the same
/// <c>%LOCALAPPDATA%\DevToolbox\Config</c> folder the UI does.
/// <para>
/// It exists rather than reusing <c>YamlStorageService</c> for three reasons, and each one is a
/// defect in this host rather than a preference:
/// </para>
/// <list type="number">
/// <item><b>It writes to stdout.</b> <c>YamlStorageService.LoadAsync</c> has a
/// <c>Console.WriteLine</c> in its catch block. In the UI that is a stray diagnostic; here STDOUT
/// IS THE JSON-RPC WIRE, and one such line corrupts the session with a symptom — a client that
/// mysteriously fails — that looks nothing like its cause.</item>
/// <item><b>Its constructor seeds config.</b> It calls <c>ConfigDefaults.SeedInto</c>, which copies
/// bundled defaults for <em>every</em> feature. A read-only server that quietly writes a dozen
/// files into the dev's config folder the first time an agent lists templates is doing something
/// nobody asked it to.</item>
/// <item><b>Delete is unreachable here.</b> <see cref="DeleteAsync"/> throws. Guardrail #1 is the
/// absence of a code path, and no tool on this server has any business removing a config file.</item>
/// </list>
/// <para>
/// The serializer settings MUST match <c>YamlStorageService</c>'s exactly — camelCase naming, and
/// unmatched properties ignored because these files are hand-edited. A mismatch would not throw;
/// it would silently parse a hand-written config into defaults, which is the worst of both.
/// </para>
/// </summary>
internal sealed class McpYamlStorage : IYamlStorageService
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public string StorageDirectory { get; }

    /// <param name="storageDirectory">
    /// Defaults to the UI's config folder, which is the point — the agent should see the same
    /// templates, locations and saved queries the dev sees. Overridable for tests.
    /// </param>
    internal McpYamlStorage(string? storageDirectory = null)
    {
        StorageDirectory = storageDirectory ?? DefaultStorageDirectory;

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    internal static string DefaultStorageDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevToolbox",
        "Config");

    private string PathFor(string fileName) => Path.Combine(StorageDirectory, $"{fileName}.yaml");

    /// <summary>
    /// Returns <c>default</c> for a file that is not there, exactly as the UI's implementation
    /// does — a missing config is an empty config, not a failure.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string fileName)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path)) return default;

        var yaml = await File.ReadAllTextAsync(path);

        // No Console anywhere, including here. A malformed file throws, ToolErrors turns it into a
        // message the caller can read, and nothing is written to the wire on the way.
        return _deserializer.Deserialize<T>(yaml);
    }

    /// <summary>
    /// Writes through a temp file and keeps one <c>.bak</c> generation — both copied deliberately
    /// from <c>YamlStorageService</c> rather than simplified away.
    /// <para>
    /// The <c>.bak</c> matters more here than it does in the UI. Serialization keeps neither
    /// comments nor key order, so any save rewrites a hand-annotated config into bare generated
    /// YAML — and the save that does it was requested by an agent rather than by the person whose
    /// comments they were. One generation back is what makes that recoverable.
    /// </para>
    /// </summary>
    public async Task SaveAsync<T>(string fileName, T data)
    {
        Directory.CreateDirectory(StorageDirectory);

        var path = PathFor(fileName);
        var yaml = _serializer.Serialize(data);

        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A backup that cannot be written must not block the save, same as the UI.
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, yaml);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Not available. See the type-level remarks — this is guardrail #1, not an omission.</summary>
    public Task<bool> DeleteAsync(string fileName) =>
        throw new NotSupportedException("Deleting configuration is not available on this server.");

    public Task<List<string>> ListFilesAsync()
    {
        if (!Directory.Exists(StorageDirectory))
            return Task.FromResult(new List<string>());

        var names = Directory.GetFiles(StorageDirectory, "*.yaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(names);
    }
}
