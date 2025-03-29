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
            // Convert to YAML directly
            var yaml = _yamlSerializer.Serialize(data);

            // Save to file
            var filePath = Path.Combine(_storageDirectory, $"{fileName}.yaml");
            await File.WriteAllTextAsync(filePath, yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save YAML file: {ex.Message}", ex);
        }
    }

    public async Task<T?> LoadAsync<T>(string fileName)
    {
        try
        {
            var filePath = Path.Combine(_storageDirectory, $"{fileName}.yaml");
            Console.WriteLine($"Attempting to load YAML from: {filePath}");
            
            if (!File.Exists(filePath))
            {
                Console.WriteLine("File does not exist");
                return default;
            }

            // Read YAML file
            var yaml = await File.ReadAllTextAsync(filePath);
            Console.WriteLine($"Read YAML content: {yaml}");

            // Deserialize directly to target type
            var result = _yamlDeserializer.Deserialize<T>(yaml);
            Console.WriteLine($"Successfully deserialized to type {typeof(T).Name}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading YAML: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
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