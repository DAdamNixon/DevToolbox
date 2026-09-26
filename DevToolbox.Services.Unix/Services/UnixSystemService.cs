using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <summary>
/// Opening files, folders, terminals and configured programs on Linux and macOS.
/// <para>
/// The Windows version is <c>SystemService</c> in DevToolbox.Services.Windows, which goes through
/// Explorer, Windows Terminal and cmd. Here the desktop's own opener does that job: <c>xdg-open</c>
/// on Linux, <c>open</c> on macOS, each of which hands a file to whatever the desktop has
/// associated with it.
/// </para>
/// </summary>
public sealed class UnixSystemService : ISystemService
{
    /// <summary>Locator results, so discovering an install costs one process per session.</summary>
    private static readonly ConcurrentDictionary<string, string?> LocatorCache = new();

    private readonly PowerShellService _powerShellService;

    public UnixSystemService(PowerShellService powerShellService)
    {
        _powerShellService = powerShellService;
    }

    private static string Opener => OperatingSystem.IsMacOS() ? "open" : "xdg-open";

    public Task<OpenResult> OpenLocationAsync(string path) => Task.Run(() =>
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return OpenResult.Fail($"Path not found: {path}");
        }

        return Start(Opener, [path], $"Could not open {path}");
    });

    public Task<OpenResult> OpenInExplorerAsync(string path) => Task.Run(async () =>
    {
        if (Directory.Exists(path))
        {
            return Start(Opener, [path], $"Could not open {path}");
        }

        if (!File.Exists(path))
        {
            return OpenResult.Fail($"Directory not found: {path}");
        }

        // Selecting the file is more useful than opening its folder and leaving you to find it.
        if (OperatingSystem.IsMacOS())
        {
            return Start("open", ["-R", path], $"Could not show {path} in Finder");
        }

        return await ShowItemAsync(path).ConfigureAwait(false)
            ? OpenResult.Ok()
            : Start(Opener, [Path.GetDirectoryName(path)!], $"Could not open the folder around {path}");
    });

    /// <summary>
    /// Asks the file manager to open the folder with <paramref name="path"/> selected, through the
    /// freedesktop FileManager1 interface that Nautilus, Dolphin, Nemo and Thunar all implement.
    /// False when nothing answers, and the caller opens the folder instead.
    /// </summary>
    private static async Task<bool> ShowItemAsync(string path)
    {
        if (UnixCommands.Resolve("gdbus") is not { } gdbus) return false;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (exitCode, _, _) = await UnixCommands.RunAsync(gdbus,
            [
                "call", "--session",
                "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems",
                $"['{new Uri(path).AbsoluteUri.Replace("'", "%27")}']", "",
            ], timeout.Token).ConfigureAwait(false);

            return exitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException || UnixCommands.IsNotFound(ex))
        {
            return false;
        }
    }

    public Task<OpenResult> OpenInTerminalAsync(string path) => Task.Run(() =>
    {
        if (!Directory.Exists(path))
        {
            return OpenResult.Fail($"Directory not found: {path}");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Start("open", ["-a", "Terminal", path], $"Could not open a terminal at {path}");
        }

        if (Terminal() is not { } terminal)
        {
            return OpenResult.Fail("No terminal program was found. Set $TERMINAL, or install gnome-terminal, konsole or xterm.");
        }

        // Every terminal starts in its working directory; gnome-terminal is told as well, because a
        // second window from an already-running server otherwise opens wherever that server was.
        var arguments = Path.GetFileName(terminal) == "gnome-terminal"
            ? new[] { $"--working-directory={path}" }
            : Array.Empty<string>();

        return Start(terminal, arguments, $"Could not open a terminal at {path}", workingDirectory: path);
    });

    /// <summary>The terminal the desktop prefers, else the first one installed.</summary>
    private static string? Terminal() => UnixCommands.FirstInstalled(
        Environment.GetEnvironmentVariable("TERMINAL"),
        "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "kitty", "alacritty", "xterm");

    public Task<OpenResult> OpenWithCustomAppAsync(string path, CustomOpenOption option, int? line = null) =>
        Task.Run(() => option.Type == OpenOptionType.Executable
            ? RunExecutable(path, option, line)
            : RunCommand(path, option, line));

    private static OpenResult RunExecutable(string path, CustomOpenOption option, int? line)
    {
        // A locator wins over a literal path, as on Windows: it is there because the literal
        // answer is wrong or moves between versions.
        var resolved = RunLocator(option.ExecutableFrom) ?? UnixCommands.Resolve(option.ExecutablePath);

        if (resolved is null)
        {
            if (string.IsNullOrWhiteSpace(option.ExecutablePath) && option.ExecutableFrom is null)
            {
                return OpenResult.Fail($"\"{option.Name}\" has no executablePath or executableFrom configured.");
            }

            return OpenResult.Fail(
                $"Could not locate \"{option.Name}\". Tried " +
                (option.ExecutableFrom is not null ? $"`{option.ExecutableFrom.Command}` and " : string.Empty) +
                $"\"{option.ExecutablePath}\" on PATH and disk.");
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = resolved, UseShellExecute = false };

            // Deliberately Replace, not string.Format: an argument template is user text and a
            // stray brace would make string.Format throw.
            if (string.IsNullOrWhiteSpace(option.Arguments))
            {
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.Arguments = SubstituteTokens(option.Arguments, path, line);
            }

            using var _ = System.Diagnostics.Process.Start(startInfo);
            return OpenResult.Ok();
        }
        catch (Exception ex)
        {
            return OpenResult.Fail($"Could not launch {resolved}: {ex.Message}");
        }
    }

    private static OpenResult RunCommand(string path, CustomOpenOption option, int? line)
    {
        if (string.IsNullOrWhiteSpace(option.Command))
        {
            return OpenResult.Fail($"\"{option.Name}\" has no command configured.");
        }

        var (shell, arguments) = CommandShell(SubstituteTokens(option.Command!, path, line));
        return Start(shell, arguments, $"Could not run \"{option.Name}\"");
    }

    /// <summary>
    /// How a configured command runs. A command is PowerShell on Windows, so it is PowerShell here
    /// too when <c>pwsh</c> is installed; without it, the POSIX shell, which is what a command
    /// written on this machine will be in.
    /// </summary>
    private static (string Shell, string[] Arguments) CommandShell(string command) =>
        UnixCommands.Resolve("pwsh") is { } pwsh
            ? (pwsh, ["-NoProfile", "-NonInteractive", "-Command", command])
            : ("/bin/sh", ["-c", command]);

    public async Task<CommandResult> RunToCompletionAsync(CustomOpenOption option, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(option);

        string file;
        IEnumerable<string> arguments;

        if (option.Type == OpenOptionType.Command)
        {
            if (string.IsNullOrWhiteSpace(option.Command)) return CommandResult.Failed($"\"{option.Name}\" has no command configured.");
            (file, var shellArguments) = CommandShell(option.Command!);
            arguments = shellArguments;
        }
        else
        {
            var resolved = RunLocator(option.ExecutableFrom) ?? UnixCommands.Resolve(option.ExecutablePath);
            if (resolved is null) return CommandResult.Failed($"Could not locate \"{option.Name}\".");
            file = resolved;
            arguments = SplitArguments(option.Arguments);
        }

        try
        {
            var (exitCode, output, error) = await UnixCommands.RunAsync(file, arguments, cancellationToken).ConfigureAwait(false);
            output = output.Trim();
            error = error.Trim();

            if (exitCode == 0) return new CommandResult(true, 0, output, null);

            return new CommandResult(
                true,
                exitCode,
                output,
                error.Length > 0 ? error : output.Length > 0 ? output : $"Exited with code {exitCode}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException || UnixCommands.IsNotFound(ex))
        {
            return CommandResult.Failed($"Could not run \"{option.Name}\": {ex.Message}");
        }
    }

    public Task<OpenResult> ExecuteScriptAsync(string scriptName, Dictionary<string, object> parameters) => Task.Run(() =>
    {
        var scriptPath = Path.Combine(_powerShellService.ScriptsDirectory, $"{scriptName}.ps1");

        if (!File.Exists(scriptPath))
        {
            return OpenResult.Fail($"Script '{scriptName}.ps1' was not found in {_powerShellService.ScriptsDirectory}.");
        }

        var projectPath = parameters.TryGetValue("ProjectPath", out var value) ? value?.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(projectPath))
        {
            return OpenResult.Fail($"'{scriptName}' needs a path to run against, and none was supplied.");
        }

        // The Scripts tab runs a script inside the app, with no PowerShell install needed. This is
        // the other way: a terminal window that stays open, which needs PowerShell itself.
        if (UnixCommands.Resolve("pwsh") is not { } pwsh)
        {
            return OpenResult.Fail(
                "Opening a script in a terminal needs PowerShell (pwsh), which is not installed. " +
                "Run it from the Scripts tab instead, or install PowerShell: https://aka.ms/install-powershell");
        }

        if (Terminal() is not { } terminal)
        {
            return OpenResult.Fail("No terminal program was found. Set $TERMINAL, or install gnome-terminal, konsole or xterm.");
        }

        var command = new[] { pwsh, "-NoExit", "-Command", ScriptCommand(scriptPath, parameters) };
        var workingDirectory = Directory.Exists(projectPath) ? projectPath : null;

        // gnome-terminal takes the command after "--"; the rest take it after "-e".
        var arguments = Path.GetFileName(terminal) == "gnome-terminal"
            ? new[] { "--" }.Concat(command)
            : new[] { "-e" }.Concat(command);

        return Start(terminal, arguments, $"Could not run '{scriptName}'", workingDirectory);
    });

    /// <summary>
    /// The script called with its parameters, each as a single-quoted PowerShell string, which is how
    /// the Windows launcher passes them and for the same reason: <c>-Command</c> parses the quoting,
    /// so no script has to strip quotes from its own arguments.
    /// </summary>
    internal static string ScriptCommand(string scriptPath, IReadOnlyDictionary<string, object> parameters)
    {
        var builder = new StringBuilder($"& '{scriptPath.Replace("'", "''")}'");

        foreach (var parameter in parameters)
        {
            // Doubling is how a single-quoted PowerShell string escapes a quote.
            var text = parameter.Value?.ToString()?.Replace("'", "''") ?? string.Empty;
            builder.Append($" -{parameter.Key} '{text}'");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fills an argument or command template: <c>{0}</c> is the file path, <c>{1}</c> the 1-based
    /// line, and a missing line is 1, which every editor reads as the top of the file.
    /// </summary>
    internal static string SubstituteTokens(string template, string path, int? line) =>
        template
            .Replace("{0}", path)
            .Replace("{1}", (line is > 0 ? line.Value : 1).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// An argument string as separate arguments, honouring double quotes, which is how the same
    /// string is split on Windows.
    /// </summary>
    internal static IEnumerable<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) yield break;

        var current = new StringBuilder();
        var quoted = false;
        var any = false;

        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) yield return current.ToString();
                current.Clear();
                any = false;
                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any) yield return current.ToString();
    }

    /// <summary>
    /// Runs a locator command and takes the first line of its output as the program's path. Most
    /// locators in a shared config are Windows programs, which simply are not found here and fall
    /// through to the literal path.
    /// </summary>
    private static string? RunLocator(ExecutableLocator? locator)
    {
        if (locator is null || string.IsNullOrWhiteSpace(locator.Command)) return null;

        var cacheKey = $"{locator.Command} {locator.Arguments}";
        if (LocatorCache.TryGetValue(cacheKey, out var cached)) return cached;

        string? result = null;

        if (UnixCommands.Resolve(locator.Command) is { } executable)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (_, output, _) = UnixCommands.RunAsync(executable, SplitArguments(locator.Arguments), timeout.Token)
                    .GetAwaiter().GetResult();

                var first = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().Trim('"'))
                    .FirstOrDefault(line => line.Length > 0);

                if (first is not null && File.Exists(first)) result = first;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException || UnixCommands.IsNotFound(ex))
            {
                Console.Error.WriteLine($"UnixSystemService: locator '{locator.Command}' failed — {ex.Message}");
            }
        }

        LocatorCache[cacheKey] = result;
        return result;
    }

    private static OpenResult Start(string program, IEnumerable<string> arguments, string failure, string? workingDirectory = null)
    {
        try
        {
            UnixCommands.StartDetached(program, arguments, workingDirectory);
            return OpenResult.Ok();
        }
        catch (Exception ex) when (UnixCommands.IsNotFound(ex))
        {
            return OpenResult.Fail($"{failure}: {program} is not installed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return OpenResult.Fail($"{failure}: {ex.Message}");
        }
    }
}
