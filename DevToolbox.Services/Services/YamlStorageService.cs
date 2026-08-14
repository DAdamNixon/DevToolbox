using System.Text.Json;
using DevToolbox.Services.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DevToolbox.Services.Services;

public class YamlStorageService : IYamlStorageService
{
    private readonly string _storageDirectory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;

    public YamlStorageService()
    {
        // Use AppData/Local directory for configuration storage
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevToolbox"
        );
        _storageDirectory = Path.Combine(appDataPath, "Config");
        
        // Create directories if they don't exist
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(_storageDirectory);

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            // These files are meant to be hand-edited; an unknown key should be ignored
            // rather than throw and take the whole page down.
            .IgnoreUnmatchedProperties()
            .Build();

        // Migrate any existing files from the old location
        MigrateFromOldLocation();
    }

    private void MigrateFromOldLocation()
    {
        try
        {
            var oldStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
            
            if (Directory.Exists(oldStoragePath))
            {
                foreach (var file in Directory.GetFiles(oldStoragePath, "*.yaml"))
                {
                    var fileName = Path.GetFileName(file);
                    var newPath = Path.Combine(_storageDirectory, fileName);
                    
                    // Only copy if file doesn't exist in new location
                    if (!File.Exists(newPath))
                    {
                        File.Copy(file, newPath);
                    }
                }
                
                // Optionally, delete the old directory after migration
                try
                {
                    Directory.Delete(oldStoragePath, true);
                }
                catch
                {
                    // Ignore deletion errors
                }
            }
        }
        catch
        {
            // Ignore migration errors
        }
    }

    public async Task SaveAsync<T>(string fileName, T data)
    {
        try
        {
            var yaml = _yamlSerializer.Serialize(data);
            var filePath = Path.Combine(_storageDirectory, $"{fileName}.yaml");

            KeepPreviousVersion(filePath);

            // Write to a temp file and swap, so an interrupted write cannot leave a
            // half-serialized config that the next load rejects as malformed.
            var tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, yaml);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException($"Failed to save YAML file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Copies the current file to <c>&lt;name&gt;.yaml.bak</c> before it is replaced.
    /// <para>
    /// These files are meant to be hand-edited and commented, and serialization
    /// keeps neither comments nor key order — so any save from the UI rewrites a
    /// carefully annotated config into bare generated YAML. The backup is what
    /// makes that recoverable instead of final. One generation is kept
    /// deliberately: the point is to survive the save you did not mean to make,
    /// not to be a version history.
    /// </para>
    /// </summary>
    private static void KeepPreviousVersion(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Copy(filePath, filePath + ".bak", overwrite: true);
        }
        catch (IOException)
        {
            // A backup that cannot be written must not block the save itself.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task<T?> LoadAsync<T>(string fileName)
    {
        try
        {
            var filePath = Path.Combine(_storageDirectory, $"{fileName}.yaml");

            if (!File.Exists(filePath))
            {
                return default;
            }

            var yaml = await File.ReadAllTextAsync(filePath);

            // Deliberately not logging the content: workspaceGroups.yaml is ~190 KB and
            // was being dumped to the console on every load, several times per page.
            return _yamlDeserializer.Deserialize<T>(yaml);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading {fileName}.yaml: {ex.Message}");
            throw new InvalidOperationException($"Failed to load YAML file: {ex.Message}", ex);
        }
    }

    public async Task<bool> DeleteAsync(string fileName)
    {
        try
        {
            var filePath = Path.Combine(_storageDirectory, $"{fileName}.yaml");
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath));
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<List<string>> ListFilesAsync()
    {
        try
        {
            return await Task.Run(() => Directory.GetFiles(_storageDirectory, "*.yaml")
                                                .Select(Path.GetFileNameWithoutExtension)
                                                .Where(name => name != null)
                                                .Select(name => name!)
                                                .ToList());
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}

// Custom type converter for workspace IDs
public class WorkspaceIdConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(int) && type.Name == "Id";
    }

    public object? ReadYaml(IParser parser, Type type)
    {
        var scalar = parser.Consume<Scalar>();
        return int.TryParse(scalar.Value, out var id) ? id : 1;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type)
    {
        var id = value?.ToString() ?? "1";
        emitter.Emit(new Scalar(id));
    }
} 