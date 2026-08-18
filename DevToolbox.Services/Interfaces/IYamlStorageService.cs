namespace DevToolbox.Services.Interfaces;

public interface IYamlStorageService
{
    /// <summary>
    /// Where the YAML actually is. Exposed because more than one thing needs to be able to say it
    /// out loud — Settings shows it so a bug report can name the right folder — and computing the
    /// path a second time somewhere else is how the two copies drift apart.
    /// </summary>
    string StorageDirectory { get; }

    Task SaveAsync<T>(string fileName, T data);
    Task<T?> LoadAsync<T>(string fileName);
    Task<bool> DeleteAsync(string fileName);
    Task<List<string>> ListFilesAsync();
} 