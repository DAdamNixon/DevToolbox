using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.UI.Models;
using Microsoft.AspNetCore.Components;

namespace DevToolbox.UI.Pages;

/// <summary>
/// The Host Changer tab. Renders whatever <see cref="IHostsFileService"/> currently knows and asks
/// it to make changes; it holds no state of its own beyond what is on screen.
/// <para>
/// The service is a singleton shared with the tray icon, so a switch made from either shows up in
/// both. Following the Service Pulse pattern, this page subscribes to the service's event rather
/// than polling — and marshals it, because the event is raised from a background loop.
/// </para>
/// </summary>
public partial class HostChanger : ComponentBase, IDisposable
{
    [Inject] private IHostsFileService Hosts { get; set; } = null!;
    [Inject] private IHostsSettingsService Settings { get; set; } = null!;

    private HostsSnapshot? Snapshot => Hosts.Current;

    private string HostsPath => Hosts.HostsPath;

    private string? LoadError => Hosts.LoadError;

    private bool Busy { get; set; }

    private string? Status { get; set; }

    private string StatusClass { get; set; } = "is-info";

    private string StatusIcon { get; set; } = "bi-info-circle-fill";

    // ── the change waiting to be confirmed ───────────────────────────────────

    private HostsChangePreview? PendingPreview { get; set; }

    private HostsSeverityLevel PendingSeverity { get; set; }

    private bool IncludeSuspect { get; set; }

    private string? _pendingGroup;
    private string? _pendingOption;

    /// <summary>An example of the markers this reads, shown when a file has none.</summary>
    private const string SampleAnnotation = """
        ##key:DB Server
        ##value:Test
        203.0.113.80    db01.example.com
        ##value:Live:warn
        # 198.51.100.11  db01.example.com
        ##clear
        """;

    // ── authoring ────────────────────────────────────────────────────────────

    private bool AddVisible { get; set; }

    private HostsAddMode AddMode { get; set; }

    private string? AddGroupName { get; set; }

    /// <summary>The option a copy is being seeded from, when the dialog is in copy mode.</summary>
    private string? CopyFromOption { get; set; }

    // ── editing ──────────────────────────────────────────────────────────────

    private bool EditOptionVisible { get; set; }

    private bool EditGroupVisible { get; set; }

    private string? EditGroupName { get; set; }

    private string? EditOptionName { get; set; }

    // ── the icon picker ──────────────────────────────────────────────────────

    private bool IconPickerVisible { get; set; }

    private string? IconPickerGroup { get; set; }

    /// <summary>
    /// Settings, held so the group icons can be resolved during a synchronous render. Refreshed
    /// whenever this page changes them; nothing else writes them.
    /// </summary>
    private HostsSettings? _settings;

    protected override async Task OnInitializedAsync()
    {
        Hosts.Changed += OnHostsChanged;

        // Normally already done at app start; this covers opening the tab before that has finished,
        // and is a no-op once initialised.
        await Hosts.InitializeAsync();

        _settings = await Settings.GetAsync();
    }

    /// <summary>
    /// Raised from a poll loop or a file watcher, so this is not on the UI thread.
    /// </summary>
    private void OnHostsChanged(object? sender, HostsSnapshotChangedEventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            // Somebody edited the file behind us. Any preview on screen was computed against the old
            // content, so it is no longer what would be written.
            if (!e.CausedByUs && PendingPreview is not null)
            {
                ClearPending();
                Notify("The hosts file changed on disk, so the pending change was dropped.", Level.Caution);
            }

            StateHasChanged();
        });
    }

    private async Task RefreshAsync()
    {
        try
        {
            await Hosts.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(ex.Message, Level.Danger);
        }
    }

    // ── switching ────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out what the switch would do and either asks first or gets on with it.
    /// </summary>
    private async Task RequestSwitchAsync((string Group, string? Option) choice)
    {
        if (Busy || Snapshot is null) return;

        HostsChangePreview preview;

        try
        {
            preview = Hosts.Preview(Snapshot, choice.Group, choice.Option);
        }
        catch (KeyNotFoundException ex)
        {
            Notify(ex.Message, Level.Danger);
            return;
        }

        if (preview.IsNoOp)
        {
            Notify(choice.Option is null
                ? $"{choice.Group} is already off."
                : $"{choice.Group} is already set to {choice.Option}.", Level.Info);
            return;
        }

        _pendingGroup = choice.Group;
        _pendingOption = choice.Option;
        IncludeSuspect = false;
        PendingSeverity = SeverityOf(choice.Group, choice.Option);

        var settings = await Settings.GetAsync();

        // A dangerous option and a blocked change always ask, whatever the setting says: those are
        // exactly the two cases where a developer needs to see the lines first.
        if (settings.ConfirmBeforeApply || preview.IsBlocked || PendingSeverity == HostsSeverityLevel.Danger)
        {
            PendingPreview = preview;
            return;
        }

        await ApplyAsync(includeSuspect: false);
    }

    /// <summary>
    /// Recomputes the preview when the sweep toggle changes, so the diff on screen is always the one
    /// that would be written.
    /// </summary>
    private Task SetIncludeSuspectAsync(bool include)
    {
        IncludeSuspect = include;

        if (Snapshot is not null && _pendingGroup is not null)
        {
            PendingPreview = Hosts.Preview(Snapshot, _pendingGroup, _pendingOption, include);
        }

        return Task.CompletedTask;
    }

    private Task ApplyPendingAsync() => ApplyAsync(IncludeSuspect);

    private async Task ApplyAsync(bool includeSuspect)
    {
        if (_pendingGroup is null) return;

        var group = _pendingGroup;
        var option = _pendingOption;

        Busy = true;

        try
        {
            var result = await Hosts.ApplyAsync(group, option, new HostsApplyOptions(includeSuspect));
            Report(result, Describe(group, option));
        }
        finally
        {
            Busy = false;
            ClearPending();
        }
    }

    private void CancelPending() => ClearPending();

    private void ClearPending()
    {
        PendingPreview = null;
        _pendingGroup = null;
        _pendingOption = null;
        IncludeSuspect = false;
    }

    // ── the repair ───────────────────────────────────────────────────────────

    private async Task InsertClearAsync(int beforeLine)
    {
        if (Busy) return;

        Busy = true;

        try
        {
            var result = await Hosts.InsertClearAsync(beforeLine);
            Report(result, $"the group closed at line {beforeLine}");
        }
        finally
        {
            Busy = false;
        }
    }

    // ── adding ───────────────────────────────────────────────────────────────

    private void BeginAddGroup() => OpenAdd(HostsAddMode.Group, null);

    private void BeginAddOption(string group) => OpenAdd(HostsAddMode.Option, group);

    private void BeginCopyOption((string Group, string Option) source) =>
        OpenAdd(HostsAddMode.Copy, source.Group, source.Option);

    private void OpenAdd(HostsAddMode mode, string? group, string? copyFrom = null)
    {
        if (Busy) return;

        AddMode = mode;
        AddGroupName = group;
        CopyFromOption = copyFrom;
        AddVisible = true;
    }

    private void CancelAdd() => AddVisible = false;

    private async Task AddAsync(HostsAddition addition)
    {
        Busy = true;

        try
        {
            var result = await Hosts.AddAsync(addition);
            Report(result, addition.Describe());
        }
        finally
        {
            Busy = false;
            AddVisible = false;
        }
    }

    // ── editing ──────────────────────────────────────────────────────────────

    private void BeginEditOption((string Group, string Option) target)
    {
        if (Busy) return;

        EditGroupName = target.Group;
        EditOptionName = target.Option;
        EditOptionVisible = true;
    }

    private void BeginEditGroup(string group)
    {
        if (Busy) return;

        EditGroupName = group;
        EditOptionName = null;
        EditGroupVisible = true;
    }

    private void CancelEdit()
    {
        EditOptionVisible = false;
        EditGroupVisible = false;
    }

    private async Task EditAsync(HostsEdit edit)
    {
        Busy = true;

        try
        {
            var result = await Hosts.EditAsync(edit);
            Report(result, edit.Describe());
        }
        finally
        {
            Busy = false;
            CancelEdit();
        }
    }

    // ── icons ────────────────────────────────────────────────────────────────

    private string IconFor(HostsGroup group) =>
        Snapshot is null
            ? HostsGroupIcons.Fallback
            : HostsGroupIcons.Resolve(group, Snapshot.Map, _settings?.GroupIcons);

    private void BeginPickIcon(string group)
    {
        IconPickerGroup = group;
        IconPickerVisible = true;
    }

    private void CancelPickIcon() => IconPickerVisible = false;

    private string IconPickerCurrent()
    {
        if (IconPickerGroup is null || Snapshot?.Map.Find(IconPickerGroup) is not { } group)
        {
            return HostsGroupIcons.Fallback;
        }

        return HostsGroupIcons.Resolve(group, Snapshot.Map, _settings?.GroupIcons);
    }

    private bool IconPickerIsPinned() =>
        IconPickerGroup is not null && HostsGroupIcons.IsPinned(IconPickerGroup, _settings?.GroupIcons);

    /// <param name="icon">Null goes back to deriving the icon from the group's entries.</param>
    private async Task ChooseIconAsync(string? icon)
    {
        var group = IconPickerGroup;
        IconPickerVisible = false;

        if (group is null) return;

        var settings = await Settings.GetAsync();

        if (icon is null) settings.GroupIcons.Remove(group);
        else settings.GroupIcons[group] = icon;

        await Settings.SaveAsync(settings);
        _settings = settings;
    }

    // ── opening ──────────────────────────────────────────────────────────────

    private async Task OpenFileAsync()
    {
        var result = await Hosts.OpenHostsFileAsync();
        if (!result.Success) Notify(result.Error ?? "The hosts file could not be opened.", Level.Danger);
    }

    private async Task OpenFolderAsync()
    {
        var result = await Hosts.OpenHostsFolderAsync();
        if (!result.Success) Notify(result.Error ?? "The folder could not be opened.", Level.Danger);
    }

    // ── presentation ─────────────────────────────────────────────────────────

    private HostsSeverityLevel SeverityOf(string group, string? option) =>
        option is null || Snapshot is null
            ? HostsSeverityLevel.Normal
            : Snapshot.Map.Find(group, option)?.Severity ?? HostsSeverityLevel.Normal;

    private static string Describe(string group, string? option) =>
        option is null ? $"{group} turned off" : $"{group} switched to {option}";

    private string SummaryLine()
    {
        if (Snapshot is null) return string.Empty;

        var on = Snapshot.Map.Groups
            .Where(group => group.ActiveOptions.Count > 0)
            .Select(group => $"{group.Name}: {group.Describe()}")
            .ToArray();

        return on.Length == 0
            ? "Nothing is switched on."
            : string.Join("   ·   ", on);
    }

    private void Report(HostsApplyResult result, string what)
    {
        switch (result.Status)
        {
            case HostsApplyStatus.Applied:
                var flush = result.AfterApplyMessage is null ? string.Empty : $" {result.AfterApplyMessage}";
                Notify($"{Capitalise(what)}.{flush}", Level.Info);
                break;

            case HostsApplyStatus.NoChange:
                Notify("Nothing needed changing.", Level.Info);
                break;

            case HostsApplyStatus.BlockedByAnomaly:
                Notify("That change was not made: it would have touched lines outside the option.", Level.Caution);
                break;

            case HostsApplyStatus.ElevationDeclined:
                Notify("Elevation was declined, so the hosts file was not changed.", Level.Caution);
                break;

            case HostsApplyStatus.Conflict:
                Notify(result.Error ?? "The hosts file changed on disk. Reload and try again.", Level.Caution);
                break;

            default:
                Notify(result.Error ?? "The change could not be made.", Level.Danger);
                break;
        }
    }

    private enum Level
    {
        Info,
        Caution,
        Danger,
    }

    private void Notify(string message, Level level)
    {
        Status = message;

        // Complete literal class strings, because Tailwind cannot see one built at runtime.
        (StatusClass, StatusIcon) = level switch
        {
            Level.Danger => (string.Empty, "bi-exclamation-triangle-fill"),
            Level.Caution => ("is-caution", "bi-exclamation-triangle-fill"),
            _ => ("is-info", "bi-info-circle-fill"),
        };
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public void Dispose() => Hosts.Changed -= OnHostsChanged;
}
