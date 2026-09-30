using DevToolbox.Services.Models;
using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>
/// The words on a load in progress. Two places show them now — the big loading card and the drawer
/// over streaming results — so they are pinned here rather than by eye in either.
/// </summary>
public class LogIngestProgressTextTests
{
    private static LogIngestProgress Reading(TimeSpan? eta = null, int stalled = 0) => new()
    {
        Phase = LogIngestPhase.Ingesting,
        FilesTotal = 380,
        FilesDone = 142,
        BytesTotal = 3L * 1024 * 1024 * 1024,
        BytesDone = 1200L * 1024 * 1024,
        Eta = eta,
        FilesStalled = stalled
    };

    [Fact]
    public void No_report_yet_reads_as_preparing() =>
        Assert.Equal("Preparing search…", LogIngestProgressText.Headline(null));

    [Fact]
    public void Reading_names_files_and_bytes() =>
        Assert.Equal("Reading 142 of 380 files · 1.2 GB of 3 GB", LogIngestProgressText.Headline(Reading()));

    [Fact]
    public void Listing_reports_what_it_has_looked_at()
    {
        var p = new LogIngestProgress { Phase = LogIngestPhase.Listing, CurrentFile = "EE IIS", ItemsExamined = 1500, FilesDone = 12 };
        Assert.Equal("Listing files in EE IIS — 1,500 checked, 12 matched", LogIngestProgressText.Headline(p));
    }

    [Fact]
    public void Eta_says_estimating_until_there_is_evidence() =>
        Assert.Equal("estimating…", LogIngestProgressText.Eta(Reading()));

    [Fact]
    public void Eta_with_a_stall_carries_both() =>
        Assert.Equal("~40s left · 1 stalled", LogIngestProgressText.Eta(Reading(TimeSpan.FromSeconds(40), stalled: 1)));

    [Fact]
    public void Eta_with_everything_stalled_is_only_the_stall() =>
        Assert.Equal("2 stalled", LogIngestProgressText.Eta(Reading(eta: null, stalled: 2)));

    private static FileProgressSnapshot InFlight(string name, FileIngestState state) =>
        new() { FileKey = name, FileName = name, LocationName = "Web01", State = state };

    [Fact]
    public void Eta_for_the_drawer_leaves_the_stall_to_its_own_button()
    {
        Assert.Equal("~40s left", LogIngestProgressText.Eta(Reading(TimeSpan.FromSeconds(40), stalled: 1), includeStalled: false));

        // Nothing is moving, so there is no estimate to give — and no stall count either, since the
        // drawer shows that as a button beside it.
        var allStalled = new LogIngestProgress
        {
            Phase = LogIngestPhase.Ingesting, FilesTotal = 3, BytesTotal = 100, BytesDone = 10, FilesStalled = 2,
            InFlight = new[] { InFlight("a", FileIngestState.Stalled), InFlight("b", FileIngestState.Stalled) }
        };
        Assert.Equal("", LogIngestProgressText.Eta(allStalled, includeStalled: false));
    }

    [Fact]
    public void Eta_for_the_drawer_is_still_estimating_while_some_files_move()
    {
        var someStalled = new LogIngestProgress
        {
            Phase = LogIngestPhase.Ingesting, FilesTotal = 3, BytesTotal = 100, BytesDone = 10, FilesStalled = 1,
            InFlight = new[] { InFlight("a", FileIngestState.Stalled), InFlight("b", FileIngestState.Reading) }
        };
        Assert.Equal("estimating…", LogIngestProgressText.Eta(someStalled, includeStalled: false));
    }

    [Fact]
    public void Querying_has_no_estimate() =>
        Assert.Equal("", LogIngestProgressText.Eta(new LogIngestProgress { Phase = LogIngestPhase.Querying, Eta = TimeSpan.FromSeconds(3) }));

    [Fact]
    public void A_file_still_opening_says_so()
    {
        var file = new FileProgressSnapshot { FileKey = "k", FileName = "u_ex260914.log", LocationName = "Web01", State = FileIngestState.Opening };
        Assert.Equal("u_ex260914.log — connecting…", LogIngestProgressText.DescribeFile(file));
    }

    [Fact]
    public void A_stalled_file_says_how_long_since_it_moved()
    {
        var file = new FileProgressSnapshot
        {
            FileKey = "k", FileName = "u_ex260914.log", LocationName = "Web01", State = FileIngestState.Stalled,
            BytesRead = 512 * 1024, BytesTotal = 2 * 1024 * 1024, SinceLastAdvance = TimeSpan.FromSeconds(31)
        };
        Assert.Equal("u_ex260914.log — 512 KB of 2 MB — last progress 31s ago", LogIngestProgressText.DescribeFile(file));
    }
}
