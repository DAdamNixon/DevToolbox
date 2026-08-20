using DevToolbox.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The guard is only as good as the agreement between two processes about a name, and every way it
/// can fail is a way of computing that name differently in each of them.
/// </summary>
public class SingleInstanceKeyTests
{
    private const string Installed =
        @"C:\Program Files\WindowsApps\ElliottElectricSupply.DevToolbox_0.4.0.0_x64__abc\DevToolbox.UI.exe";

    private const string Working =
        @"C:\TFS\Tools\DevToolbox\DevToolbox.UI\bin\Debug\net9.0-windows\win-x64\DevToolbox.UI.exe";

    [Fact]
    public void The_same_path_always_gives_the_same_name()
    {
        Assert.Equal(SingleInstanceKey.MutexName(Installed), SingleInstanceKey.MutexName(Installed));
    }

    [Theory]
    [InlineData(@"c:\apps\devtoolbox\DevToolbox.UI.exe", @"C:\Apps\DevToolbox\DevToolbox.UI.EXE")]
    [InlineData(@"C:\apps\DevToolbox.UI.exe", @"C:/apps/DevToolbox.UI.exe")]
    [InlineData(@"C:\apps\DevToolbox.UI.exe", @"  C:\apps\DevToolbox.UI.exe  ")]
    public void Differences_that_are_not_differences_do_not_split_the_instance(string a, string b)
    {
        // Windows paths are case-insensitive, either separator addresses the same file, and
        // surrounding whitespace is an artefact of however the path was passed in.
        Assert.Equal(SingleInstanceKey.MutexName(a), SingleInstanceKey.MutexName(b));
    }

    [Fact]
    public void A_working_copy_and_the_installed_package_are_separate_instances()
    {
        // Deliberate: pressing F5 must not silently foreground the installed app instead.
        Assert.NotEqual(SingleInstanceKey.MutexName(Installed), SingleInstanceKey.MutexName(Working));
    }

    [Fact]
    public void The_mutex_and_the_message_are_not_the_same_name()
    {
        Assert.NotEqual(SingleInstanceKey.MutexName(Installed), SingleInstanceKey.ShowWindowMessageName(Installed));
    }

    [Fact]
    public void The_message_name_is_per_copy_too()
    {
        // A broadcast must not reach a copy that was allowed to run separately.
        Assert.NotEqual(
            SingleInstanceKey.ShowWindowMessageName(Installed),
            SingleInstanceKey.ShowWindowMessageName(Working));
    }

    [Fact]
    public void The_mutex_name_is_session_scoped()
    {
        // Global\ would mean one instance per machine, so a second person over RDP could not open
        // DevToolbox at all.
        Assert.StartsWith(@"Local\", SingleInstanceKey.MutexName(Installed));
    }

    [Fact]
    public void The_path_does_not_appear_in_the_name()
    {
        var name = SingleInstanceKey.MutexName(Installed);

        // A backslash separates namespaces in a kernel object name, so only the one in the Local\
        // prefix may be there — an embedded path would be rejected or, worse, silently reinterpreted.
        Assert.Equal(1, name.Count(c => c == '\\'));
        Assert.DoesNotContain("DevToolbox.UI.exe", name);
        Assert.DoesNotContain("Program Files", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_path_still_yields_one_usable_name(string? path)
    {
        var name = SingleInstanceKey.MutexName(path);

        Assert.StartsWith(@"Local\DevToolbox.SingleInstance.", name);
        Assert.Equal(name, SingleInstanceKey.MutexName(path));
    }

    [Fact]
    public void Names_stay_well_within_the_length_a_kernel_object_allows()
    {
        var deep = @"C:\" + string.Join('\\', Enumerable.Repeat("a-fairly-long-directory-name", 20)) + @"\DevToolbox.UI.exe";

        Assert.True(SingleInstanceKey.MutexName(deep).Length < 260);
    }
}
