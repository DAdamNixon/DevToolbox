using System.Collections.Generic;
using System.Linq;
using DevToolbox.Services.Models;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// The YAML the editor leaves behind, asserted as text.
/// <para>
/// These files stay hand-editable — that is the whole reason they are YAML and not a database — so
/// what a save writes has to be readable afterwards. The keys are also a contract with every
/// existing file on every machine: <c>logLocations</c>, <c>namePattern</c>. Emit <c>LogLocations</c>
/// instead and a config written by the UI stops being readable by the app that wrote it.
/// </para>
/// </summary>
public class LogConfigYamlShapeTests
{
    private static readonly ISerializer Serializer =
        new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    [Fact]
    public void A_saved_template_uses_the_same_keys_the_shipped_files_do()
    {
        var yaml = Serializer.Serialize(new LogTemplate
        {
            Name = "WebsiteBase",
            Extension = ".txt",
            Delimiter = "|",
            Columns = new List<string> { "DateTime", "Guid" },
            Sort = new List<SortColumn> { new() { Column = "DateTime", Direction = "asc" } }
        });

        // Pinned as whole text, not key by key: the point is that the file stays as readable as
        // the hand-written ones it replaces. Note the delimiter is quoted — a bare | opens a YAML
        // block scalar — and that `inherits` is absent rather than blank.
        Assert.Equal(
            """
            name: WebsiteBase
            extension: .txt
            delimiter: '|'
            columns:
            - DateTime
            - Guid
            sort:
            - column: DateTime
              direction: asc

            """,
            yaml,
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void A_template_with_no_inherits_and_no_sort_writes_neither_key()
    {
        var yaml = Serializer.Serialize(new LogTemplate
        {
            Name = "Plain Log Rows",
            Extension = ".log",
            Delimiter = "",
            Columns = new List<string> { "text" }
        });

        Assert.DoesNotContain("inherits", yaml);
        Assert.DoesNotContain("sort", yaml);
        Assert.Contains("delimiter: ''", yaml);
    }

    [Fact]
    public void A_saved_location_list_uses_the_keys_log_paths_yaml_already_has()
    {
        var yaml = Serializer.Serialize(new LogLocationConfig
        {
            LogLocations = new List<LogLocation>
            {
                new() { Name = "Live Web01", Path = @"\\web01\inetpub\LogFiles", NamePattern = @"^(?<name>.+)\.txt$" }
            }
        });

        Assert.Contains("logLocations:", yaml);
        Assert.Contains("name: Live Web01", yaml);
        Assert.Contains("path:", yaml);
        Assert.Contains("namePattern:", yaml);
    }

    [Fact]
    public void A_location_with_no_pattern_writes_no_namePattern_key()
    {
        var yaml = Serializer.Serialize(new LogLocationConfig
        {
            LogLocations = new List<LogLocation> { new() { Name = "EOX Logs", Path = @"\\eox01\Logs" } }
        });

        Assert.DoesNotContain("namePattern", yaml);
    }

    [Fact]
    public void A_backslash_path_survives_the_round_trip_byte_for_byte()
    {
        const string path = @"\\fileserver01\LogFiles\WebServers\ElliottLogs";

        var yaml = Serializer.Serialize(new LogLocationConfig
        {
            LogLocations = new List<LogLocation> { new() { Name = "Archived", Path = path } }
        });

        var back = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<LogLocationConfig>(yaml);

        // UNC paths are the normal case here and a serializer that decided to escape them would be
        // caught by nothing else — the app would just look in a folder that does not exist.
        Assert.Equal(path, back.LogLocations.Single().Path);
    }

    [Fact]
    public void A_regex_with_its_own_backslashes_survives_the_round_trip()
    {
        const string pattern = @"^(?<name>.+)\.(?<date>\d{8})\.WEB(?<server>\d+)\.txt$";

        var yaml = Serializer.Serialize(new LogLocationConfig
        {
            LogLocations = new List<LogLocation> { new() { Name = "Archived", Path = @"C:\Logs", NamePattern = pattern } }
        });

        var back = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<LogLocationConfig>(yaml);

        Assert.Equal(pattern, back.LogLocations.Single().NamePattern);
    }
}
