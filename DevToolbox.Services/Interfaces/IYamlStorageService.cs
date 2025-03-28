namespace DevToolbox.Services.Interfaces;

public interface IYamlStorageService
{
    Task SaveAsync<T>(string fileName, T data);
    Task<T?> LoadAsync<T>(string fileName);
    Task<bool> DeleteAsync(string fileName);
    Task<List<string>> ListFilesAsync();
} 