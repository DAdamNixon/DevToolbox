using DevToolbox.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    public class LogFileService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;

        public LogFileService(IYamlStorageService yamlStorage)
        {
            _yamlStorage = yamlStorage;
        }

        public async Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync()
        {
            var config = await _yamlStorage.LoadAsync<LogTemplateIndexConfig>("log_templates_index") ?? new LogTemplateIndexConfig();
            return config.Templates;
        }

        public async Task<List<LogLocation>> GetLogLocationsAsync()
        {
            var config = await _yamlStorage.LoadAsync<LogLocationConfig>("log_paths") ?? new LogLocationConfig();
            return config.LogLocations;
        }

        public async Task<LogTemplate?> LoadTemplateAsync(string fileName)
        {
            return await _yamlStorage.LoadAsync<LogTemplate>(Path.GetFileNameWithoutExtension(fileName));
        }

        public async Task<List<Dictionary<string, string>>> SearchLogFilesAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName
        )
        {
            var results = new List<Dictionary<string, string>>();
            if (!Directory.Exists(location))
                return results;

            // Find the template entry by name
            var templateEntries = await GetAvailableLogFileTemplatesAsync();
            var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
            if (templateEntry == null)
                throw new Exception($"Template '{templateName}' not found in index.");

            var template = await LoadTemplateAsync(templateEntry.File);
            if (template == null)
                throw new Exception($"Template file '{templateEntry.File}' could not be loaded.");

            var columns = await ResolveColumnsAsync(template);

            var files = await Task.Run(() => Directory.GetFiles(location, $"{logFile}*.txt"));
            foreach (var file in files)
            {
                var fileInfo = new System.IO.FileInfo(file);
                var fileDate = fileInfo.LastWriteTime;
                if (fileDate.Date >= startDate.Date && fileDate.Date <= endDate.Date)
                {
                    var lines = await File.ReadAllLinesAsync(file);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(template.Delimiter);

                        var dict = new Dictionary<string, string>();
                        for (int i = 0; i < columns.Count; i++)
                        {
                            dict[columns[i]] = parts.Length > i ? parts[i] : "";
                        }
                        // If there are extra columns, add them as Message N
                        for (int i = columns.Count; i < parts.Length; i++)
                        {
                            dict[$"Message {i - columns.Count + 1}"] = parts[i];
                        }
                        results.Add(dict);
                    }
                }
            }
            return results;
        }

        private async Task<List<string>> ResolveColumnsAsync(LogTemplate template)
        {
            if (!string.IsNullOrWhiteSpace(template.Inherits))
            {
                var baseTemplate = await _yamlStorage.LoadAsync<LogTemplate>(template.Inherits);
                if (baseTemplate != null)
                {
                    var merged = new List<string>(baseTemplate.Columns);
                    merged.AddRange(template.Columns);
                    return merged;
                }
            }
            return template.Columns;
        }
    }
}