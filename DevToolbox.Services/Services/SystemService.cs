using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.Win32;
using System.Text;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Service for handling system-level operations like opening files, folders, and executing external processes
    /// </summary>
    public class SystemService : ISystemService
    {
        private static readonly string[] ScriptExtensions = { ".cmd", ".bat" };

        /// <summary>Locator results, so discovering an install costs one process per session.</summary>
        private static readonly ConcurrentDictionary<string, string?> LocatorCache = new();

        private readonly PowerShellService _powerShellService;

        public SystemService(PowerShellService powerShellService)
        {
            _powerShellService = powerShellService;
        }

        /// <summary>
        /// Opens a file or folder location using the default system application
        /// </summary>
        public async Task<OpenResult> OpenLocationAsync(string path)
        {
            return await Task.Run(() =>
            {
                if (File.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                        return OpenResult.Ok();
                    }
                    catch (Win32Exception ex)
                    {
                        // Nothing is registered for this extension. Common for
                        // .code-workspace, which VS Code only associates when the
                        // installer's "register as editor for supported file types" box
                        // was ticked. Say so — the fix is an openWith on the source.
                        var extension = Path.GetExtension(path);
                        return OpenResult.Fail(
                            $"Windows has no app associated with {(string.IsNullOrEmpty(extension) ? "this file" : extension)}. " +
                            $"Give the workspace source an \"openWith\" app, or use Open With from the menu. ({ex.Message})");
                    }
                    catch (Exception ex)
                    {
                        return OpenResult.Fail($"Could not open {path}: {ex.Message}");
                    }
                }

                if (Directory.Exists(path))
                {
                    return StartExplorer(path);
                }

                return OpenResult.Fail($"Path not found: {path}");
            });
        }

        /// <summary>
        /// Opens a folder in Windows Explorer
        /// </summary>
        public async Task<OpenResult> OpenInExplorerAsync(string path)
        {
            return await Task.Run(() =>
            {
                if (!Directory.Exists(path))
                {
                    // Selecting the file is more useful than refusing outright.
                    if (File.Exists(path))
                    {
                        try
                        {
                            Process.Start("explorer.exe", $"/select,\"{path}\"");
                            return OpenResult.Ok();
                        }
                        catch (Exception ex)
                        {
                            return OpenResult.Fail($"Could not open Explorer at {path}: {ex.Message}");
                        }
                    }

                    return OpenResult.Fail($"Directory not found: {path}");
                }

                return StartExplorer(path);
            });
        }

        /// <summary>
        /// Opens a folder in Windows Terminal
        /// </summary>
        public async Task<OpenResult> OpenInTerminalAsync(string path)
        {
            return await Task.Run(() =>
            {
                if (!Directory.Exists(path))
                {
                    return OpenResult.Fail($"Directory not found: {path}");
                }

                try
                {
                    Process.Start("wt.exe", $"-d \"{path}\"");
                    return OpenResult.Ok();
                }
                catch (Win32Exception)
                {
                    // Windows Terminal is not installed everywhere; fall back to the
                    // shell that always is.
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoExit",
                            WorkingDirectory = path,
                            UseShellExecute = true
                        });
                        return OpenResult.Ok();
                    }
                    catch (Exception ex)
                    {
                        return OpenResult.Fail($"Could not open a terminal at {path}: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    return OpenResult.Fail($"Could not open a terminal at {path}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Opens a location with a custom application
        /// </summary>
        public async Task<OpenResult> OpenWithCustomAppAsync(string path, CustomOpenOption option, int? line = null)
        {
            return await Task.Run(() => option.Type == OpenOptionType.Executable
                ? RunExecutable(path, option, line)
                : RunCommand(path, option, line));
        }

        private static OpenResult StartExplorer(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"\"{path}\"");
                return OpenResult.Ok();
            }
            catch (Exception ex)
            {
                return OpenResult.Fail($"Could not open Explorer at {path}: {ex.Message}");
            }
        }

        private static OpenResult RunExecutable(string path, CustomOpenOption option, int? line = null)
        {
            // A locator wins over a literal path: it is there precisely because the
            // literal answer is wrong or moves between versions.
            var resolved = RunLocator(option.ExecutableFrom);

            if (resolved is null && !string.IsNullOrWhiteSpace(option.ExecutablePath))
            {
                resolved = ResolveExecutable(option.ExecutablePath!);
            }

            if (resolved is null)
            {
                if (string.IsNullOrWhiteSpace(option.ExecutablePath) && option.ExecutableFrom is null)
                {
                    return OpenResult.Fail($"\"{option.Name}\" has no executablePath or executableFrom configured.");
                }

                return OpenResult.Fail(
                    $"Could not locate \"{option.Name}\". Tried " +
                    (option.ExecutableFrom is not null ? $"`{option.ExecutableFrom.Command}` and " : string.Empty) +
                    $"\"{option.ExecutablePath}\" on PATH, App Paths and disk.");
            }

            try
            {
                var startInfo = BuildStartInfo(resolved, path, option.Arguments, line);
                Process.Start(startInfo);
                return OpenResult.Ok();
            }
            catch (Exception ex)
            {
                return OpenResult.Fail($"Could not launch {resolved}: {ex.Message}");
            }
        }

        private static ProcessStartInfo BuildStartInfo(string executable, string path, string argumentTemplate, int? line = null)
        {
            // A .cmd/.bat is not a real executable, so CreateProcess cannot run it
            // directly — it has to go through cmd.exe. `code` on PATH is exactly this
            // case: it is code.cmd, not an exe.
            var isScript = ScriptExtensions.Contains(Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase);

            // Deliberately Replace, not string.Format: an argument template is user text
            // and a stray brace would make string.Format throw.
            var arguments = string.IsNullOrWhiteSpace(argumentTemplate)
                ? null
                : SubstituteTokens(argumentTemplate, path, line);

            if (isScript)
            {
                // cmd's /c quoting: the whole command is wrapped in one extra pair.
                var inner = arguments is null
                    ? $"\"{executable}\" \"{path}\""
                    : $"\"{executable}\" {arguments}";

                return new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{inner}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (arguments is null)
            {
                // ArgumentList quotes each entry correctly, including paths with spaces.
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.Arguments = arguments;
            }

            return startInfo;
        }

        /// <summary>
        /// Fills an argument or command template: <c>{0}</c> is the file path,
        /// <c>{1}</c> the 1-based line.
        /// <para>
        /// With no line to substitute, <c>{1}</c> collapses to <c>1</c> rather than
        /// being left in place — every editor treats line 1 as "the top of the
        /// file", whereas a literal "{1}" reaching the command line is an error the
        /// user cannot do anything about.
        /// </para>
        /// </summary>
        private static string SubstituteTokens(string template, string path, int? line) =>
            template
                .Replace("{0}", path)
                .Replace("{1}", (line is > 0 ? line.Value : 1).ToString(CultureInfo.InvariantCulture));

        private static OpenResult RunCommand(string path, CustomOpenOption option, int? line = null)
        {
            if (string.IsNullOrWhiteSpace(option.Command))
            {
                return OpenResult.Fail($"\"{option.Name}\" has no command configured.");
            }

            var command = SubstituteTokens(option.Command!, path, line);

            try
            {
                // -EncodedCommand sidesteps quoting entirely. Passing the command inline
                // as -Command "..." broke the moment the command itself contained double
                // quotes, which any command taking a path does.
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                return OpenResult.Ok();
            }
            catch (Exception ex)
            {
                return OpenResult.Fail($"Could not run \"{option.Name}\": {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a locator command and takes the first line of its output as the path to
        /// an executable. Results are cached — this spawns a process, and the Open button
        /// should not pay for that on every click.
        /// </summary>
        private static string? RunLocator(ExecutableLocator? locator)
        {
            if (locator is null || string.IsNullOrWhiteSpace(locator.Command))
            {
                return null;
            }

            var cacheKey = $"{locator.Command} {locator.Arguments}";
            if (LocatorCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            string? result = null;

            try
            {
                var executable = ResolveExecutable(locator.Command);
                if (executable is not null)
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = locator.Arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });

                    if (process is not null)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        if (!process.WaitForExit(15000))
                        {
                            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                        }

                        var first = output
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim().Trim('"'))
                            .FirstOrDefault(line => line.Length > 0);

                        if (first is not null && File.Exists(first))
                        {
                            result = first;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SystemService: locator '{locator.Command}' failed — {ex.Message}");
            }

            LocatorCache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// Resolves an executable the way a shell would: an explicit path is used as-is,
        /// a bare name is looked up across PATH with each PATHEXT extension tried, then
        /// against the App Paths registry that the Run dialog uses.
        /// <para>
        /// .NET's <c>Process.Start</c> with <c>UseShellExecute = false</c> does not apply
        /// PATHEXT, so <c>code</c> would fail even though it works in a terminal.
        /// </para>
        /// </summary>
        private static string? ResolveExecutable(string nameOrPath)
        {
            var candidate = Environment.ExpandEnvironmentVariables(nameOrPath.Trim().Trim('"'));

            if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
            }

            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);

            var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var directory in directories)
            {
                string full;
                try
                {
                    full = Path.Combine(directory.Trim().Trim('"'), candidate);
                }
                catch (ArgumentException)
                {
                    continue; // A malformed PATH entry should not stop the search.
                }

                if (Path.HasExtension(candidate) && File.Exists(full))
                {
                    return full;
                }

                foreach (var extension in extensions)
                {
                    if (File.Exists(full + extension))
                    {
                        return full + extension;
                    }
                }
            }

            return FromAppPaths(candidate);
        }

        /// <summary>
        /// Last resort: the App Paths registry, which is how Start &gt; Run resolves a bare
        /// program name. Entries here can be stale or point at an older install, so the
        /// target is verified to exist and a locator command should be preferred when the
        /// right answer actually matters.
        /// </summary>
        private static string? FromAppPaths(string candidate)
        {
            var name = Path.HasExtension(candidate) ? candidate : candidate + ".exe";
            var subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}";

            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(subKey);
                    var value = key?.GetValue(null) as string;

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // No read access to that hive — try the next one.
                }
            }

            return null;
        }

        /// <summary>
        /// Executes a PowerShell script with the given parameters
        /// </summary>
        public async Task ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters)
        {
            try
            {
                Console.WriteLine($"SystemService: Executing PowerShell script: {scriptName}");

                string scriptPath = Path.Combine(_powerShellService.ScriptsDirectory, $"{scriptName}.ps1");

                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"Script file '{scriptName}.ps1' not found.");
                    return;
                }

                // Simplified for just ProjectPath
                string projectPath = parameters.ContainsKey("ProjectPath") ? parameters["ProjectPath"].ToString() : "";
                if (string.IsNullOrEmpty(projectPath))
                {
                    Console.WriteLine("ProjectPath parameter is required but was not provided or empty.");
                    return;
                }

                // Build the PowerShell command with parameters
                var paramString = new StringBuilder();
                foreach (var param in parameters)
                {
                    // Properly escape single quotes in values by doubling them
                    string escapedValue = param.Value.ToString().Replace("'", "''");
                    paramString.Append($"-{param.Key} '{escapedValue}' ");
                }

                // Start PowerShell with the script and parameters
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // -NoExit keeps the window open after script execution
                    Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{scriptPath}\" {paramString}",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Process.Start(startInfo);
                Console.WriteLine($"PowerShell script '{scriptName}' launched in a new terminal window.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing script: {scriptName}. Error: {ex.Message}");
            }
        }
    }
}
