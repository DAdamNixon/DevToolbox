using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The guard that stops the elevated step being a general-purpose "copy anything anywhere as
/// administrator" tool.
/// <para>
/// The elevated run will copy a staged file over a system file. Everything protecting that rests on
/// this check, so it is tested for the ways someone would try to get around it rather than only for
/// the happy path.
/// </para>
/// </summary>
public class HostsPathsTests
{
    private static string Root => HostsPaths.RequestRoot;

    [Fact]
    public void A_directory_inside_the_request_root_is_accepted()
    {
        Assert.True(HostsPaths.IsInsideRequestRoot(Path.Combine(Root, "abc123")));
    }

    [Fact]
    public void The_request_root_itself_is_not_inside_itself()
    {
        // Requests live in a subdirectory. Accepting the root would let one request see another's
        // staged payload.
        Assert.False(HostsPaths.IsInsideRequestRoot(Root));
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_the_same_is_rejected()
    {
        // The classic prefix mistake: "…\HostsWriteElsewhere" starts with "…\HostsWrite".
        Assert.False(HostsPaths.IsInsideRequestRoot(Root + "Elsewhere"));
        Assert.False(HostsPaths.IsInsideRequestRoot(Path.Combine(Root + "Elsewhere", "abc")));
    }

    [Fact]
    public void Traversal_out_of_the_request_root_is_rejected()
    {
        Assert.False(HostsPaths.IsInsideRequestRoot(Path.Combine(Root, "..", "..", "somewhere")));
        Assert.False(HostsPaths.IsInsideRequestRoot(Path.Combine(Root, "abc", "..", "..", "..", "Windows")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_is_rejected(string? candidate)
    {
        Assert.False(HostsPaths.IsInsideRequestRoot(candidate!));
    }

    [Fact]
    public void An_unrelated_path_is_rejected()
    {
        Assert.False(HostsPaths.IsInsideRequestRoot(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")));
    }

    [Fact]
    public void A_malformed_path_is_rejected_rather_than_throwing()
    {
        Assert.False(HostsPaths.IsInsideRequestRoot("|<>*?"));
    }

    /// <summary>
    /// A link inside the staging folder could point anywhere, which would make the containment check
    /// meaningless. Skipped when the test host cannot create one — that needs either developer mode
    /// or elevation, and the check itself is exercised by the cases above.
    /// </summary>
    [Fact]
    public void A_directory_link_inside_the_request_root_is_rejected()
    {
        Directory.CreateDirectory(Root);

        var link = Path.Combine(Root, "link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), "target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            Assert.False(HostsPaths.IsInsideRequestRoot(link));
        }
        finally
        {
            try { Directory.Delete(link); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(target); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Every_working_folder_sits_under_the_applications_own_app_data()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox");

        Assert.Equal(appData, HostsPaths.AppDataRoot);
        Assert.StartsWith(appData, HostsPaths.RequestRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appData, HostsPaths.BackupRoot, StringComparison.OrdinalIgnoreCase);
    }
}
