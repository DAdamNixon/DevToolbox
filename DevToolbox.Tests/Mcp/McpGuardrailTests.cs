using DevToolbox.Mcp.Core;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests.Mcp;

/// <summary>The row cap: clamp above the ceiling, reject at or below zero.</summary>
public sealed class RowCapTests
{
    [Fact]
    public void An_omitted_page_size_is_the_default_not_the_ceiling()
    {
        // The distinction is deliberate. An agent that did not ask for a page size wants to see the
        // shape of the rows, not the largest page the server will produce.
        Assert.Equal(RowCap.Default, RowCap.Clamp(null));
        Assert.True(RowCap.Default < RowCap.Max);
    }

    [Fact]
    public void A_page_size_above_the_ceiling_is_clamped_rather_than_refused()
    {
        Assert.Equal(RowCap.Max, RowCap.Clamp(RowCap.Max + 1));
        Assert.Equal(RowCap.Max, RowCap.Clamp(1_000_000));
    }

    [Fact]
    public void A_page_size_within_range_is_honoured()
    {
        Assert.Equal(1, RowCap.Clamp(1));
        Assert.Equal(RowCap.Max, RowCap.Clamp(RowCap.Max));
    }

    [Fact]
    public void Zero_or_negative_is_rejected_rather_than_silently_honoured()
    {
        // An empty page is indistinguishable from "the query matched nothing", so honouring 0 would
        // dress an ambiguous answer up as a real one.
        Assert.Throws<ArgumentOutOfRangeException>(() => RowCap.Clamp(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowCap.Clamp(-5));
    }

    [Fact]
    public void The_refusal_names_the_requested_value_and_the_ceiling()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RowCap.Clamp(0));

        Assert.Contains("0", ex.Message);
        Assert.Contains(RowCap.Max.ToString(), ex.Message);
    }
}

/// <summary>Exception text on its way to a caller.</summary>
public sealed class SafeErrorTests
{
    [Fact]
    public void A_windows_path_is_removed_from_the_message()
    {
        // Paths here run through %LOCALAPPDATA%, which contains the Windows account name. Not a
        // catastrophe; also not something to hand out on every I/O error.
        var scrubbed = SafeError.Scrub(@"Could not find file 'C:\Users\SomeUser\AppData\Local\DevToolbox\Config\x.yaml'.");

        Assert.DoesNotContain("SomeUser", scrubbed);
        Assert.DoesNotContain(@"C:\", scrubbed);
        Assert.Contains(SafeError.Redacted, scrubbed);
    }

    [Fact]
    public void A_unc_path_is_removed_too()
    {
        var scrubbed = SafeError.Scrub(@"Access to \\fileserver01\LogFiles\thing.txt is denied.");

        Assert.DoesNotContain("fileserver01", scrubbed);
        Assert.Contains(SafeError.Redacted, scrubbed);
    }

    [Fact]
    public void The_exception_type_survives_because_it_is_the_useful_part()
    {
        // "IOException" versus "SqliteException" is the difference between "the file moved" and
        // "your query was wrong", and neither name reveals anything.
        var described = SafeError.Describe(new InvalidOperationException("no such column: Nope"));

        Assert.Contains(nameof(InvalidOperationException), described);
        Assert.Contains("no such column", described);
    }

    [Fact]
    public void Text_with_no_path_in_it_is_left_alone()
    {
        const string message = "near \"SELCT\": syntax error";
        Assert.Equal(message, SafeError.Scrub(message));
    }
}

/// <summary>
/// Table handles — the only caller-supplied string on this server that reaches SQL as an
/// identifier, and therefore the only one that has to be refused rather than escaped.
/// </summary>
public sealed class PreparedTablesTests
{
    [Fact]
    public void A_generated_handle_contains_nothing_that_could_end_an_identifier()
    {
        for (var i = 0; i < 50; i++)
        {
            var handle = PreparedTables.NewHandle();

            Assert.StartsWith("logs_", handle);
            Assert.All(handle, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_', $"unexpected character '{c}'"));
        }
    }

    [Fact]
    public void Handles_are_unique()
    {
        var handles = Enumerable.Range(0, 200).Select(_ => PreparedTables.NewHandle()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(200, handles.Count);
    }

    [Fact]
    public void A_handle_the_server_never_issued_is_refused()
    {
        var tables = new PreparedTables();
        Assert.Throws<UnknownHandleException>(() => tables.Resolve("logs_deadbeefdeadbeef"));
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("logs_a]; DROP TABLE logs; --")]
    [InlineData("logs_a' OR '1'='1")]
    [InlineData("sqlite_master")]
    [InlineData("logs_短")]
    [InlineData("")]
    [InlineData(null)]
    public void A_handle_that_is_not_our_shape_is_refused_before_any_lookup(string? handle)
    {
        // Shape first, registry second. Both hold independently: the shape check would still refuse
        // an injection attempt if the registry were bypassed, and the registry would still refuse a
        // well-shaped handle it never issued.
        var tables = new PreparedTables();
        Assert.Throws<UnknownHandleException>(() => tables.Resolve(handle));
    }

    [Fact]
    public void An_issued_handle_resolves_to_what_was_registered()
    {
        var tables = new PreparedTables();
        var handle = PreparedTables.NewHandle();
        tables.Register(new PreparedTable(handle, "Checkout", "WebsiteBase", new[] { "DateTime" }, 42, DateTime.UtcNow));

        var resolved = tables.Resolve(handle);

        Assert.Equal("Checkout", resolved.LogFile);
        Assert.Equal(42, resolved.Rows);
    }

    [Fact]
    public void The_refusal_does_not_enumerate_other_handles()
    {
        // A caller that lost a handle should prepare again. Listing what else exists tells it about
        // other work in the session, which is not its business.
        var tables = new PreparedTables();
        var mine = PreparedTables.NewHandle();
        tables.Register(new PreparedTable(mine, "Secret", "WebsiteBase", Array.Empty<string>(), 1, DateTime.UtcNow));

        var ex = Assert.Throws<UnknownHandleException>(() => tables.Resolve("logs_0000000000000000"));

        Assert.DoesNotContain(mine, ex.Message);
        Assert.DoesNotContain("Secret", ex.Message);
    }
}

/// <summary>
/// The read-only storage instance. Two independent layers — the writing members throw, and the
/// connection itself is opened <c>Mode=ReadOnly</c> — so neither is load-bearing alone.
/// </summary>
public sealed class ReadOnlyStorageTests
{
    [Fact]
    public async Task Every_writing_member_throws_on_a_read_only_instance()
    {
        using var temp = new TempDirectory("mcp-readonly");
        var db = Path.Combine(temp.Path, "logs.db");

        var writable = new SqliteLogStorageService(db);
        await writable.EnsureTableAsync("t", new[] { "a" });

        var readOnly = new SqliteLogStorageService(db, readOnly: true);

        await Assert.ThrowsAsync<NotSupportedException>(() => readOnly.EnsureTableAsync("t2", new[] { "a" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => readOnly.DropTableAsync("t"));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => readOnly.InsertLogLinesAsync("t", new[] { new Dictionary<string, string> { ["a"] = "1" } }));
    }

    [Fact]
    public async Task A_read_only_instance_can_still_read_what_the_writable_one_created()
    {
        // The guardrail is worthless if it also blocks the queries. This is the half that proves
        // Mode=ReadOnly actually works against a WAL database written by the sibling instance —
        // which is not obvious, and is exactly the kind of thing to verify rather than assume.
        using var temp = new TempDirectory("mcp-readonly-read");
        var db = Path.Combine(temp.Path, "logs.db");

        var writable = new SqliteLogStorageService(db);
        await writable.EnsureTableAsync("logs_test", new[] { "Message1" });
        await writable.InsertLogLinesAsync("logs_test", new[]
        {
            new Dictionary<string, string> { ["Message1"] = "hello" },
            new Dictionary<string, string> { ["Message1"] = "world" },
        });

        var readOnly = new SqliteLogStorageService(db, readOnly: true);

        Assert.True(await readOnly.TableExistsAsync("logs_test"));

        var (rows, total) = await readOnly.SearchLogsAsync("logs_test", new DevToolbox.Services.Models.LogQuery());
        Assert.Equal(2, total);
        Assert.Equal(2, rows.Count());
    }

    [Fact]
    public void A_read_only_instance_does_not_create_the_folder_it_points_at()
    {
        // Creating it would make an instance aimed at a typo look like an empty database rather
        // than a mistake.
        using var temp = new TempDirectory("mcp-readonly-nofolder");
        var missing = Path.Combine(temp.Path, "not-created", "logs.db");

        _ = new SqliteLogStorageService(missing, readOnly: true);

        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }
}
