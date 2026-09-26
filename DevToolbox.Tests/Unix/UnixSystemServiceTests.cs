using System.Runtime.Versioning;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests.Unix;

/// <summary>
/// How the Linux side starts programs. The program started is a shell script this test writes,
/// which records the arguments it was given — so nothing real opens.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSystemServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DevToolbox.Tests", Guid.NewGuid().ToString("n"));
    private readonly string _recorder;
    private readonly string _record;

    public UnixSystemServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _record = Path.Combine(_directory, "args.txt");
        _recorder = Path.Combine(_directory, "fake-editor");
        File.WriteAllText(_recorder, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{_record}'\n");
        File.SetUnixFileMode(_recorder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static UnixSystemService Service() => new(new PowerShellService());

    private async Task<string[]> RecordedAsync()
    {
        for (var i = 0; i < 50 && !File.Exists(_record); i++) await Task.Delay(50);
        await Task.Delay(50);
        return File.ReadAllLines(_record);
    }

    [UnixFact]
    public async Task With_no_argument_template_the_path_is_one_argument_even_with_spaces()
    {
        var path = Path.Combine(_directory, "My Project");
        var option = new CustomOpenOption { Name = "Fake", Type = OpenOptionType.Executable, ExecutablePath = _recorder };

        var result = await Service().OpenWithCustomAppAsync(path, option);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { path }, await RecordedAsync());
    }

    [UnixFact]
    public async Task An_argument_template_gets_the_path_and_the_line()
    {
        var path = Path.Combine(_directory, "app log.txt");
        var option = new CustomOpenOption { Name = "Fake", Type = OpenOptionType.Executable, ExecutablePath = _recorder, Arguments = "-g \"{0}:{1}\"" };

        var result = await Service().OpenWithCustomAppAsync(path, option, line: 42);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { "-g", $"{path}:42" }, await RecordedAsync());
    }

    [UnixFact]
    public async Task A_program_that_is_not_installed_says_so_rather_than_throwing()
    {
        var option = new CustomOpenOption { Name = "Nothing", Type = OpenOptionType.Executable, ExecutablePath = "no-such-program-devtoolbox" };

        var result = await Service().OpenWithCustomAppAsync(_directory, option);

        Assert.False(result.Success);
        Assert.Contains("Could not locate \"Nothing\"", result.Error);
    }

    [UnixFact]
    public async Task A_command_runs_in_a_shell_and_its_output_comes_back()
    {
        // Without pwsh installed a command is POSIX shell; with it, PowerShell — and echo works in both.
        var option = new CustomOpenOption { Name = "Echo", Type = OpenOptionType.Command, Command = "echo hello" };

        var result = await Service().RunToCompletionAsync(option);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Output);
    }

    [UnixFact]
    public async Task A_missing_path_is_refused_before_anything_starts()
    {
        var result = await Service().OpenLocationAsync(Path.Combine(_directory, "gone"));

        Assert.False(result.Success);
        Assert.StartsWith("Path not found", result.Error);
    }

    [Fact]
    public void The_script_command_quotes_every_value_for_PowerShell()
    {
        var command = UnixSystemService.ScriptCommand("/opt/scripts/it's.ps1", new Dictionary<string, object>
        {
            ["ProjectPath"] = "/home/me/My Project",
            ["Name"] = "O'Brien",
        });

        Assert.Equal("& '/opt/scripts/it''s.ps1' -ProjectPath '/home/me/My Project' -Name 'O''Brien'", command);
    }

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("-g \"/x y/z:3\"", new[] { "-g", "/x y/z:3" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("   ", new string[0])]
    public void Arguments_split_the_way_Windows_splits_them(string text, string[] expected)
    {
        Assert.Equal(expected, UnixSystemService.SplitArguments(text));
    }

    [Fact]
    public void Windows_file_filters_become_zenity_filters()
    {
        Assert.Equal(
            new[] { "--file-filter=Scripts | *.ps1 *.psm1", "--file-filter=All Files (*.*) | *" },
            UnixPathPicker.ZenityFilters("Scripts|*.ps1;*.psm1|All Files (*.*)|*.*"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
