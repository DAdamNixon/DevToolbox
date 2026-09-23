using System.Threading.Tasks;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Reads and writes <c>log_ingest_settings.yaml</c> — its own file (D7) rather than
    /// <c>UiSettings</c>, so <c>DevToolbox.Mcp</c> can load it without loading
    /// <c>DevToolbox.Services</c>.
    /// </summary>
    public static class LogIngestSettingsStore
    {
        internal const string FileName = "log_ingest_settings";

        public static async Task<LogIngestSettings> LoadAsync(IYamlStorageService yaml) =>
            await yaml.LoadAsync<LogIngestSettings>(FileName) ?? new LogIngestSettings();

        public static Task SaveAsync(IYamlStorageService yaml, LogIngestSettings settings) =>
            yaml.SaveAsync(FileName, settings);
    }
}
