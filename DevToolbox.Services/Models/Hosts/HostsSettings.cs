using DevToolbox.Services.Models;

namespace DevToolbox.Services.Models.Hosts;

/// <summary>
/// The annotation tokens, as declared in config. Defaults reproduce the legacy Toolbox format.
/// </summary>
public sealed class HostsAnnotationSettings
{
    public string Prefix { get; set; } = "##";
    public string GroupVerb { get; set; } = "key";
    public string OptionVerb { get; set; } = "value";
    public string ClearVerb { get; set; } = "clear";
    public string FlagSeparator { get; set; } = ":";
}

/// <summary>
/// Root of <c>Config/host_changer_settings.yaml</c> — the Host Changer's own preferences.
/// <para>
/// Groups and options are deliberately absent: they live in the annotated hosts file, which is
/// the single source of truth. What lives here is how to find that file, how to read its
/// dialect, and what to do around a change.
/// </para>
/// </summary>
public sealed class HostsSettings
{
    /// <summary>Blank or null uses the system hosts file. Environment variables are expanded.</summary>
    public string? HostsFilePath { get; set; }

    /// <summary>Backups to keep. Zero keeps all of them.</summary>
    public int BackupRetention { get; set; } = 25;

    /// <summary>Show the diff and ask before applying. A dangerous option always asks regardless.</summary>
    public bool ConfirmBeforeApply { get; set; } = true;

    /// <summary>Refuse a switch that would touch quarantined lines until it is confirmed.</summary>
    public bool BlockApplyOnSuspectLines { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Closing the window hides it instead of exiting. False restores close-exits.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Set once the balloon explaining hide-to-tray has been shown.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>How often to check the file for external edits, in seconds.</summary>
    public int RefreshSeconds { get; set; } = 5;

    public HostsAnnotationSettings Annotation { get; set; } = new();

    /// <summary>
    /// Flag name to severity, as written in the file. The values are
    /// <c>normal</c>, <c>caution</c> and <c>danger</c>.
    /// <para>
    /// These are the words a particular team happens to use — <c>web</c> meaning "pointed at a
    /// real web tier" is not a concept the tool has any business knowing — so they are named
    /// here rather than baked into the parser.
    /// </para>
    /// </summary>
    public Dictionary<string, string> SeverityFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warn"] = "danger",
        ["web"] = "caution",
    };

    /// <summary>Markers that park a line permanently — never enabled, never re-commented.</summary>
    public List<string> ParkedMarkers { get; set; } = [";"];

    /// <summary>
    /// Group name to icon class, e.g. <c>DB Server: bi-database</c>. Empty by default — a group
    /// nobody has named here is given an icon derived from the addresses its entries point at, so
    /// every card has one without any configuration and naming one here simply overrides it.
    /// <para>
    /// Here rather than in the hosts file on purpose. That file is shared with teammates and still
    /// read by the legacy tool, which would take an extra token on a <c>##key:</c> line as part of
    /// the group's name. An icon is a local preference; it does not belong in shared content.
    /// </para>
    /// </summary>
    public Dictionary<string, string> GroupIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive blank lines that count as a gap when detecting foreign content.</summary>
    public int UnscopedGapBlankLines { get; set; } = 2;

    /// <summary>
    /// Runs after a successful change, in the app's own non-elevated context. Seeded with a DNS
    /// cache flush; clear it to disable.
    /// <para>
    /// Never runs elevated, and that is deliberate: this is user-editable config, and config must
    /// not be able to name a command that runs as administrator.
    /// </para>
    /// </summary>
    public CustomOpenOption? AfterApply { get; set; }

    /// <summary>Opens the hosts file. Null falls back to <c>openHandlers.yaml</c> and then the shell.</summary>
    public CustomOpenOption? Editor { get; set; }

    /// <summary>
    /// The system hosts file, built from <see cref="Environment.SpecialFolder.System"/> so nothing
    /// hardcodes a Windows directory.
    /// </summary>
    public static string DefaultHostsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>The file to operate on, with environment variables expanded.</summary>
    public string ResolveHostsPath() =>
        string.IsNullOrWhiteSpace(HostsFilePath)
            ? DefaultHostsPath()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(HostsFilePath.Trim()));

    /// <summary>
    /// The dialect these settings describe. Invalid values are corrected rather than rejected, so a
    /// typo in a hand-edited file degrades instead of taking the tab down.
    /// </summary>
    public HostsDialect ToDialect() => new()
    {
        Prefix = Fallback(Annotation.Prefix, "##"),
        GroupVerb = Fallback(Annotation.GroupVerb, "key"),
        OptionVerb = Fallback(Annotation.OptionVerb, "value"),
        ClearVerb = Fallback(Annotation.ClearVerb, "clear"),
        FlagSeparator = Fallback(Annotation.FlagSeparator, ":"),
        SeverityFlags = SeverityFlags.ToDictionary(
            pair => pair.Key,
            pair => ParseSeverity(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        ParkedMarkers = ParkedMarkers.Where(marker => !string.IsNullOrEmpty(marker)).ToArray(),
        UnscopedGapBlankLines = Math.Max(1, UnscopedGapBlankLines),
    };

    /// <summary>The settings a first run writes: the defaults plus a DNS flush after a change.</summary>
    public static HostsSettings CreateStarter() => new()
    {
        AfterApply = new CustomOpenOption
        {
            Name = "Flush DNS cache",
            Type = OpenOptionType.Executable,
            ExecutablePath = "ipconfig",
            Arguments = "/flushdns",
        },
    };

    private static string Fallback(string? value, string standby) =>
        string.IsNullOrWhiteSpace(value) ? standby : value.Trim();

    /// <summary>
    /// An unrecognised severity becomes <see cref="HostsSeverityLevel.Danger"/>. A flag exists to
    /// draw attention, so a misspelt one should shout rather than go quiet.
    /// </summary>
    private static HostsSeverityLevel ParseSeverity(string? value) =>
        Enum.TryParse<HostsSeverityLevel>(value?.Trim(), ignoreCase: true, out var level)
            ? level
            : HostsSeverityLevel.Danger;
}
