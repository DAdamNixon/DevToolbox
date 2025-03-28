using System.Text.Json;
using DevToolbox.Services.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Services.Services;

public class YamlStorageService : IYamlStorageService
{
    private readonly string _storageDirectory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;

    public YamlStorageService()
    {
        _storageDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
        Directory.CreateDirectory(_storageDirectory);

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
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
            if (!File.Exists(filePath))
            {
                return default;
            }

            // Read YAML file
            var yaml = await File.ReadAllTextAsync(filePath);

            // Deserialize directly to target type
            return _yamlDeserializer.Deserialize<T>(yaml);
        }
        catch (Exception ex)
        {
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
                File.Delete(filePath);
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
            return Directory.GetFiles(_storageDirectory, "*.yaml")
                           .Select(Path.GetFileNameWithoutExtension)
                           .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
} 