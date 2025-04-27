using System.Diagnostics;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Service for handling system-level operations like opening files, folders, and executing external processes
    /// </summary>
    public class SystemService : ISystemService
    {
        private readonly PowerShellService _powerShellService;

        public SystemService(PowerShellService powerShellService)
        {
            _powerShellService = powerShellService;
        }

        /// <summary>
        /// Opens a file or folder location using the default system application
        /// </summary>
        public async Task OpenLocationAsync(string path)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        };
                        Process.Start(startInfo);
                    }
                    else if (Directory.Exists(path))
                    {
                        Process.Start("explorer.exe", path);
                    }
                    else
                    {
                        Console.WriteLine($"Path not found: {path}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening location: {path}. Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Opens a folder in Windows Explorer
        /// </summary>
        public async Task OpenInExplorerAsync(string path)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Process.Start("explorer.exe", path);
                    }
                    else
                    {
                        Console.WriteLine($"Directory not found: {path}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening explorer at: {path}. Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Opens a folder in Windows Terminal
        /// </summary>
        public async Task OpenInTerminalAsync(string path)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Process.Start("wt.exe", $"-d \"{path}\"");
                    }
                    else
                    {
                        Console.WriteLine($"Directory not found: {path}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening terminal at: {path}. Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Opens a location with a custom application
        /// </summary>
        public async Task OpenWithCustomAppAsync(string path, CustomOpenOption option)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (option.Type == OpenOptionType.Executable)
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = option.ExecutablePath,
                            Arguments = string.Format(option.Arguments, path),
                            UseShellExecute = false
                        };
                        Process.Start(startInfo);
                    }
                    else
                    {
                        var command = string.Format(option.Command ?? "", path);
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -Command \"{command}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        Process.Start(startInfo);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening with custom app: {path}. Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Executes a PowerShell script with the given parameters
        /// </summary>
        public async Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters)
        {
            try
            {
                await _powerShellService.ExecuteScriptFileAsync(scriptName, parameters);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing script: {scriptName}. Error: {ex.Message}");
            }
        }
    }
} 