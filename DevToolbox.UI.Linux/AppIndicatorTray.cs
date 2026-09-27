using System.Diagnostics;
using System.Runtime.InteropServices;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.UI.Services;

namespace DevToolbox.UI.Linux;

/// <summary>
/// The tray icon on Linux: which hosts options are switched on, at a glance, and one click to change
/// them without opening the window. It behaves like the Windows app's HostsTrayIcon — the same colours,
/// the same menu, the same refusals — and is written for the Linux desktop rather than copied.
/// <para>
/// libayatana-appindicator, called directly, on the GTK thread PhotinoX already runs. It publishes the
/// icon as a StatusNotifierItem and the menu over D-Bus, which is what GNOME's AppIndicator extension,
/// KDE and most other panels show. Every method here runs on that thread; events from elsewhere come
/// in through <c>post</c>.
/// </para>
/// </summary>
internal sealed class AppIndicatorTray : ITrayIcon
{
    private readonly IntPtr _indicator;
    private readonly IHostsFileService _hosts;
    private readonly AppShellService _shell;
    private readonly Action<Action> _post;
    private readonly Action _showWindow;
    private readonly Action? _openInBrowser;
    private readonly Action _exit;

    /// <summary>What each item of the current menu does, by the number its GTK signal carries.</summary>
    private readonly Dictionary<int, Action> _actions = [];

    private string? _iconName;
    private bool _disposed;

    /// <summary>Set by the indicator's connection-changed signal: is a panel showing it?</summary>
    public bool IsShowing { get; private set; }

    private AppIndicatorTray(
        IntPtr indicator,
        IHostsFileService hosts,
        AppShellService shell,
        Action<Action> post,
        Action showWindow,
        Action? openInBrowser,
        Action exit)
    {
        _indicator = indicator;
        _hosts = hosts;
        _shell = shell;
        _post = post;
        _showWindow = showWindow;
        _openInBrowser = openInBrowser;
        _exit = exit;
    }

    /// <summary>
    /// The tray icon, or null when the library is not installed. Call on the GTK thread, with GTK
    /// running. <paramref name="openInBrowser"/> is null when the browser view is not running.
    /// </summary>
    public static AppIndicatorTray? TryCreate(
        IHostsFileService hosts,
        AppShellService shell,
        Action<Action> post,
        Action showWindow,
        Action? openInBrowser,
        Action exit)
    {
        if (!NativeLibrary.TryLoad(Native.Indicator, out _))
        {
            Console.Error.WriteLine("No tray icon: libayatana-appindicator3 is not installed (on Ubuntu: sudo apt install libayatana-appindicator3-1).");
            return null;
        }

        var iconFolder = Icons.Write();
        var indicator = Native.app_indicator_new_with_path(
            "devtoolbox", Icons.NameFor(HostsSeverityLevel.Normal), Native.CategoryApplicationStatus, iconFolder);

        var tray = new AppIndicatorTray(indicator, hosts, shell, post, showWindow, openInBrowser, exit);
        tray.Start();
        return tray;
    }

    private void Start()
    {
        Instances[_indicator] = this;
        Native.g_signal_connect_data(_indicator, "connection-changed", Native.ConnectionChangedPointer, _indicator, IntPtr.Zero, 0);

        Native.app_indicator_set_title(_indicator, "DevToolbox");

        _hosts.Changed += OnHostsChanged;
        Refresh();

        // Shown only once it has a menu, so a panel never offers a click on an icon with nothing behind it.
        //
        // Two "gtk_widget_get_scale_factor: assertion 'GTK_IS_WIDGET (widget)' failed" lines follow at
        // startup. They come from libayatana-appindicator's own idle handler (0.5.93), with or without a
        // panel, a menu target or anything else passed in here, and are harmless.
        Native.app_indicator_set_status(_indicator, Native.StatusActive);

        // Assumed showing until the panel says otherwise: the signal only fires on a change, and on
        // a desktop with a panel the first report can come after the window's first close.
        IsShowing = true;
    }

    // ── keeping in step ──────────────────────────────────────────────────────

    /// <summary>Raised from a poll loop or a file watcher, so it is moved onto the GTK thread.</summary>
    private void OnHostsChanged(object? sender, HostsSnapshotChangedEventArgs e) => _post(Refresh);

    /// <summary>
    /// The icon's colour and a fresh menu, from the current parse. A D-Bus menu has no "about to
    /// open" moment to rebuild in, as the Windows one does, so it is rebuilt whenever the file or a
    /// switch changes instead — which comes to the same thing: it can never show a stale answer.
    /// </summary>
    private void Refresh()
    {
        if (_disposed) return;

        var map = _hosts.Current?.Map;

        var iconName = Icons.NameFor(map?.ActiveSeverity ?? HostsSeverityLevel.Normal);
        if (iconName != _iconName)
        {
            Native.app_indicator_set_icon_full(_indicator, iconName, "Host Changer");
            _iconName = iconName;
        }

        BuildMenu(map);
    }

    // ── the menu ─────────────────────────────────────────────────────────────

    private void BuildMenu(HostsMap? map)
    {
        _actions.Clear();
        var menu = Native.gtk_menu_new();

        if (map is null)
        {
            Append(menu, Disabled("Hosts file not read yet"));
        }
        else
        {
            // There is no tooltip to put the state in, as on Windows, so each group says it here.
            foreach (var group in map.Groups) Append(menu, GroupItem(group));

            if (map.Groups.Count == 0) Append(menu, Disabled("No switchable groups in this file"));
        }

        Append(menu, Native.gtk_separator_menu_item_new());
        Append(menu, Item("Open HOSTS file", () => Run(() => _hosts.OpenHostsFileAsync())));
        Append(menu, Item("Open HOSTS folder", () => Run(() => _hosts.OpenHostsFolderAsync())));
        Append(menu, Native.gtk_separator_menu_item_new());

        var openTab = Item("Open Host Changer", OpenTab);
        Append(menu, openTab);
        Append(menu, Item("Show DevToolbox", _showWindow));
        if (_openInBrowser is not null) Append(menu, Item("Open in browser", _openInBrowser));
        Append(menu, Item("Exit DevToolbox", _exit));

        Native.gtk_widget_show_all(menu);
        Native.app_indicator_set_menu(_indicator, menu);

        // A middle click on the icon, where the panel supports it: the Windows double-click.
        Native.app_indicator_set_secondary_activate_target(_indicator, openTab);
    }

    private IntPtr GroupItem(HostsGroup group)
    {
        var item = Native.gtk_menu_item_new_with_label(Label($"{group.Name}: {group.Describe()}"));
        var submenu = Native.gtk_menu_new();

        foreach (var option in group.Options)
        {
            var label = option.IsPartiallyOn ? $"{option.Name}  ({option.PartialLabel})" : option.Name;
            var captured = option.Name;
            Append(submenu, Check(label, option.IsOn, () => Switch(group, captured)));
        }

        Append(submenu, Native.gtk_separator_menu_item_new());
        Append(submenu, Check("Off", group.ActiveOptions.Count == 0, () => Switch(group, null)));

        Native.gtk_menu_item_set_submenu(item, submenu);
        return item;
    }

    /// <summary>
    /// Applies a switch from the tray. Two things are deliberately not silent, as on Windows: a group
    /// holding lines that look like they belong to something else is refused and sent to the tab,
    /// because agreeing to that needs the diff in front of you; and a dangerous option asks first.
    /// Without zenity to ask with, both go to the tab, which asks there.
    /// </summary>
    private void Switch(HostsGroup group, string? option) => Run(async () =>
    {
        if (_hosts.IsApplying) return;

        if (group.HasSuspectContent)
        {
            var review = await Dialogs.AskAsync(
                "Review this change first",
                $"'{group.Name}' claims lines that do not look like they belong to it, so switching it here "
                + "could comment out entries you rely on.\n\nOpen Host Changer to see exactly which lines are affected?",
                yes: "Open Host Changer", no: "Cancel");

            if (review is not false) _post(OpenTab);
            return;
        }

        var severity = option is null ? HostsSeverityLevel.Normal : group.Find(option)?.Severity ?? HostsSeverityLevel.Normal;
        if (severity == HostsSeverityLevel.Danger)
        {
            var confirm = await Dialogs.AskAsync(
                "Confirm switch",
                $"Switch '{group.Name}' to '{option}'?\n\nThis option is flagged as dangerous.",
                yes: "Switch", no: "Cancel");

            if (confirm is null) _post(OpenTab);
            if (confirm is not true) return;
        }

        var result = await _hosts.ApplyAsync(group.Name, option, new HostsApplyOptions());
        if (!result.Success && result.Status != HostsApplyStatus.ElevationDeclined)
        {
            var message = result.Error ?? "The change could not be made.";
            if (!await Dialogs.WarnAsync("Host Changer", message)) Notifications.Show("Host Changer", message);
        }
    });

    private void OpenTab()
    {
        _showWindow();
        _shell.RequestNavigation("/host-changer");
    }

    /// <summary>
    /// Runs a menu action off the GTK thread, and always rebuilds the menu afterwards: a check item
    /// ticks itself when clicked, and a switch that was cancelled or failed must not stay ticked.
    /// </summary>
    private void Run(Func<Task> action) => Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"Tray action failed: {ex.Message}");
        }
        finally
        {
            _post(Refresh);
        }
    });

    // ── GTK menu items ───────────────────────────────────────────────────────

    private IntPtr Item(string label, Action action) =>
        Connect(Native.gtk_menu_item_new_with_label(Label(label)), action);

    private IntPtr Check(string label, bool on, Action action)
    {
        var item = Native.gtk_check_menu_item_new_with_label(Label(label));
        // Set before connecting, so it does not count as a click.
        Native.gtk_check_menu_item_set_active(item, on);
        Native.gtk_widget_set_sensitive(item, !_hosts.IsApplying);
        return Connect(item, action);
    }

    private static IntPtr Disabled(string label)
    {
        var item = Native.gtk_menu_item_new_with_label(Label(label));
        Native.gtk_widget_set_sensitive(item, false);
        return item;
    }

    private IntPtr Connect(IntPtr item, Action action)
    {
        var id = _actions.Count + 1;
        _actions[id] = action;
        Native.g_signal_connect_data(item, "activate", Native.ActivatePointer, (IntPtr)id, IntPtr.Zero, 0);
        return item;
    }

    private static void Append(IntPtr menu, IntPtr item) => Native.gtk_menu_shell_append(menu, item);

    /// <summary>
    /// The D-Bus menu reads an underscore as the mark of a keyboard shortcut, so a group called
    /// DEV_DB would lose it. Doubled, it shows as one.
    /// </summary>
    private static string Label(string text) => text.Replace("_", "__");

    // ── callbacks from GTK ───────────────────────────────────────────────────

    /// <summary>The live tray, by its indicator, for the static callbacks GTK calls.</summary>
    private static readonly Dictionary<IntPtr, AppIndicatorTray> Instances = [];

    private static AppIndicatorTray? Current => Instances.Values.FirstOrDefault();

    private static void OnActivate(IntPtr widget, IntPtr data)
    {
        if (Current is { _disposed: false } tray && tray._actions.TryGetValue((int)data, out var action)) action();
    }

    private static void OnConnectionChanged(IntPtr indicator, int connected, IntPtr data)
    {
        if (Instances.TryGetValue(data, out var tray)) tray.IsShowing = connected != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hosts.Changed -= OnHostsChanged;
        Native.app_indicator_set_status(_indicator, Native.StatusPassive);
        Instances.Remove(_indicator);
        Native.g_object_unref(_indicator);
    }

    // ── the icons ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Windows tray's routing mark in its three colours, drawn as SVG rather than shipped as image
    /// files: nothing to keep in step with the severity levels, and nothing binary in the repository.
    /// Written to a folder the indicator is told to look in, and named per colour so a panel that
    /// caches by name still sees the change.
    /// </summary>
    private static class Icons
    {
        public static string NameFor(HostsSeverityLevel level) => level switch
        {
            HostsSeverityLevel.Danger => "devtoolbox-hosts-danger",
            HostsSeverityLevel.Caution => "devtoolbox-hosts-caution",
            _ => "devtoolbox-hosts-normal",
        };

        /// <summary>Writes the three icons and returns their folder.</summary>
        public static string Write()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox", "tray");
            Directory.CreateDirectory(folder);

            foreach (var (level, colour) in new[]
            {
                (HostsSeverityLevel.Normal, "#10B981"),
                (HostsSeverityLevel.Caution, "#F59E0B"),
                (HostsSeverityLevel.Danger, "#EF4444"),
            })
            {
                File.WriteAllText(Path.Combine(folder, NameFor(level) + ".svg"), $"""
                    <svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">
                      <circle cx="16" cy="16" r="15.5" fill="{colour}"/>
                      <g stroke="#FFFFFF" stroke-width="2.5" stroke-linecap="round">
                        <line x1="9" y1="16" x2="17" y2="9"/>
                        <line x1="9" y1="16" x2="17" y2="23"/>
                      </g>
                      <g fill="#FFFFFF">
                        <circle cx="9" cy="16" r="3"/>
                        <circle cx="18" cy="9" r="3"/>
                        <circle cx="18" cy="23" r="3"/>
                      </g>
                    </svg>
                    """);
            }

            return folder;
        }
    }

    // ── native ───────────────────────────────────────────────────────────────

    private static class Native
    {
        public const string Indicator = "libayatana-appindicator3.so.1";
        private const string Gtk = "libgtk-3.so.0";
        private const string GObject = "libgobject-2.0.so.0";

        public const int CategoryApplicationStatus = 0;
        public const int StatusPassive = 0;
        public const int StatusActive = 1;

        public delegate void ActivateHandler(IntPtr widget, IntPtr data);
        public delegate void ConnectionChangedHandler(IntPtr indicator, int connected, IntPtr data);

        // Held in static fields for the life of the process: GTK keeps the pointers, and a collected
        // delegate behind one of them would crash the first click after a garbage collection.
        private static readonly ActivateHandler Activate = OnActivate;
        private static readonly ConnectionChangedHandler ConnectionChanged = OnConnectionChanged;
        public static readonly IntPtr ActivatePointer = Marshal.GetFunctionPointerForDelegate(Activate);
        public static readonly IntPtr ConnectionChangedPointer = Marshal.GetFunctionPointerForDelegate(ConnectionChanged);

        [DllImport(Indicator)]
        public static extern IntPtr app_indicator_new_with_path(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
            int category,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iconThemePath);

        [DllImport(Indicator)]
        public static extern void app_indicator_set_status(IntPtr indicator, int status);

        [DllImport(Indicator)]
        public static extern void app_indicator_set_title(IntPtr indicator, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

        [DllImport(Indicator)]
        public static extern void app_indicator_set_icon_full(
            IntPtr indicator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iconDescription);

        [DllImport(Indicator)]
        public static extern void app_indicator_set_menu(IntPtr indicator, IntPtr menu);

        [DllImport(Indicator)]
        public static extern void app_indicator_set_secondary_activate_target(IntPtr indicator, IntPtr menuItem);

        [DllImport(Gtk)]
        public static extern IntPtr gtk_menu_new();

        [DllImport(Gtk)]
        public static extern IntPtr gtk_menu_item_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);

        [DllImport(Gtk)]
        public static extern IntPtr gtk_check_menu_item_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);

        [DllImport(Gtk)]
        public static extern void gtk_check_menu_item_set_active(IntPtr item, [MarshalAs(UnmanagedType.Bool)] bool active);

        [DllImport(Gtk)]
        public static extern IntPtr gtk_separator_menu_item_new();

        [DllImport(Gtk)]
        public static extern void gtk_menu_shell_append(IntPtr menu, IntPtr item);

        [DllImport(Gtk)]
        public static extern void gtk_menu_item_set_submenu(IntPtr item, IntPtr submenu);

        [DllImport(Gtk)]
        public static extern void gtk_widget_set_sensitive(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool sensitive);

        [DllImport(Gtk)]
        public static extern void gtk_widget_show_all(IntPtr widget);

        [DllImport(GObject)]
        public static extern ulong g_signal_connect_data(
            IntPtr instance,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
            IntPtr handler,
            IntPtr data,
            IntPtr destroyData,
            int flags);

        [DllImport(GObject)]
        public static extern void g_object_unref(IntPtr instance);
    }
}
