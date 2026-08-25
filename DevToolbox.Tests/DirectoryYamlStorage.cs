using DevToolbox.Services.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// Reads YAML out of one directory, the way <c>YamlStorageService</c> does, without its
/// constructor — which points at the real <c>%LOCALAPPDATA%</c>, migrates, and seeds it.
/// </summary>
internal sealed class DirectoryYamlStorage : IYamlStorageService
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public DirectoryYamlStorage(string directory)
    {
        StorageDirectory = directory;
    }

    public string StorageDirectory { get; }

    public Task<T?> LoadAsync<T>(string fileName)
    {
        var path = Path.Combine(StorageDirectory, $"{fileName}.yaml");
        return Task.FromResult(File.Exists(path) ? Yaml.Deserialize<T>(File.ReadAllText(path)) : default);
    }

    public Task SaveAsync<T>(string fileName, T data) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string fileName) => throw new NotSupportedException();

    public Task<List<string>> ListFilesAsync() => throw new NotSupportedException();
}

/// <summary>A directory that deletes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string label)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), label, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
