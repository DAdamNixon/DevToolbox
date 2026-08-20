using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.UI.Services;

/// <summary>
/// The notification-area icon: which hosts options are switched on, at a glance, and one click to
/// change them without opening the window.
/// <para>
/// This is the whole reason the legacy tool stayed installed. The colour is the point — pointing at
/// a live database should be visible from the taskbar rather than something you have to remember.
/// </para>
/// </summary>
public sealed class HostsTrayIcon : IDisposable
{
    /// <summary><see cref="NotifyIcon.Text"/> is silently limited to this, and five groups exceed it.</summary>
    private const int TooltipLimit = 63;

    private readonly Control _uiOwner;
    private readonly IHostsFileService _hosts;
    private readonly IHostsSettingsService _settings;
    private readonly AppShellService _shell;
    private readonly Action _showWindow;
    private readonly Action _exit;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<HostsSeverityLevel, Icon> _icons = [];

    private bool _disposed;

    public HostsTrayIcon(
        IContainer components,
        Control uiOwner,
        IHostsFileService hosts,
        IHostsSettingsService settings,
        AppShellService shell,
        Action showWindow,
        Action exit)
    {
        _uiOwner = uiOwner;
        _hosts = hosts;
        _settings = settings;
        _shell = shell;
        _showWindow = showWindow;
        _exit = exit;

        _menu = new ContextMenuStrip();

        // Rebuilt each time it opens, from the freshest parse. The file is a few kilobytes, and this
        // is how the legacy tray behaved — it means the menu can never show a stale answer.
        _menu.Opening += OnMenuOpening;

        // Registered with the form's container so disposal comes for free.
        _icon = new NotifyIcon(components)
        {
            Text = "Host Changer",
            Icon = IconFor(HostsSeverityLevel.Normal),
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => OpenTab();

        _hosts.Changed += OnHostsChanged;

        Refresh();
    }

    /// <summary>Shows a one-off balloon explaining that closing the window did not quit.</summary>
    public void ShowHiddenToTrayHint() =>
        ShowBalloon("DevToolbox is still running", "Right-click the tray icon to switch hosts, show the window, or exit.");

    /// <summary>
    /// Generic balloon for any feature that wants to reach the user without its own tray icon —
    /// Service Pulse alerts use this rather than getting a second icon next to it. The name on
    /// this class predates that; it is the one tray icon the app has.
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }

    // ── keeping in step ──────────────────────────────────────────────────────

    /// <summary>Raised from a poll loop or a file watcher, so it has to be marshalled.</summary>
    private void OnHostsChanged(object? sender, HostsSnapshotChangedEventArgs e)
    {
        if (_uiOwner.IsDisposed) return;

        try
        {
            if (_uiOwner.InvokeRequired) _uiOwner.BeginInvoke(Refresh);
            else Refresh();
        }
        catch (ObjectDisposedException)
        {
            // The window went away between the check and the call.
        }
        catch (InvalidOperationException)
        {
            // No handle yet; the next change will catch up.
        }
    }

    private void Refresh()
    {
        if (_disposed) return;

        var map = _hosts.Current?.Map;

        _icon.Icon = IconFor(map?.ActiveSeverity ?? HostsSeverityLevel.Normal);
        _icon.Text = Tooltip(map);
    }

    /// <summary>
    /// One line per group. Truncated because <see cref="NotifyIcon.Text"/> quietly refuses anything
    /// longer; the full picture is the first item of the menu.
    /// </summary>
    private static string Tooltip(HostsMap? map)
    {
        if (map is null || map.Groups.Count == 0) return "Host Changer";

        var full = string.Join(Environment.NewLine, map.Groups.Select(g => $"{g.Name}: {g.Describe()}"));

        return full.Length <= TooltipLimit ? full : full[..(TooltipLimit - 1)] + "…";
    }

    // ── the menu ─────────────────────────────────────────────────────────────

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        _menu.Items.Clear();

        var map = _hosts.Current?.Map;

        if (map is null)
        {
            _menu.Items.Add(new ToolStripMenuItem("Hosts file not read yet") { Enabled = false });
        }
        else
        {
            // The tooltip is clipped, so the full state goes here where there is room for it.
            foreach (var group in map.Groups)
            {
                _menu.Items.Add(BuildGroupItem(group));
            }

            if (map.Groups.Count == 0)
            {
                _menu.Items.Add(new ToolStripMenuItem("No switchable groups in this file") { Enabled = false });
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Open HOSTS file", async () => await _hosts.OpenHostsFileAsync()));
        _menu.Items.Add(Item("Open HOSTS folder", async () => await _hosts.OpenHostsFolderAsync()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Open Host Changer", () => { OpenTab(); return Task.CompletedTask; }));
        _menu.Items.Add(Item("Show DevToolbox", () => { _showWindow(); return Task.CompletedTask; }));
        _menu.Items.Add(Item("Exit DevToolbox", () => { _exit(); return Task.CompletedTask; }));
    }

    private ToolStripMenuItem BuildGroupItem(HostsGroup group)
    {
        var item = new ToolStripMenuItem(group.Name)
        {
            Image = IconFor(group.ActiveSeverity).ToBitmap(),
        };

        foreach (var option in group.Options)
        {
            var label = option.IsPartiallyOn ? $"{option.Name}  ({option.PartialLabel})" : option.Name;

            var entry = new ToolStripMenuItem(label)
            {
                Checked = option.IsOn,
                CheckOnClick = false,
                ToolTipText = Describe(option),
                Enabled = !_hosts.IsApplying,
            };

            var captured = option;
            entry.Click += async (_, _) => await SwitchAsync(group, captured.Name);

            item.DropDownItems.Add(entry);
        }

        item.DropDownItems.Add(new ToolStripSeparator());

        var off = new ToolStripMenuItem("Off")
        {
            Checked = group.ActiveOptions.Count == 0,
            Enabled = !_hosts.IsApplying,
            ToolTipText = "Comment out every option in this group.",
        };

        off.Click += async (_, _) => await SwitchAsync(group, null);
        item.DropDownItems.Add(off);

        return item;
    }

    private static string Describe(HostsOption option)
    {
        var lines = option.IsOn
            ? $"{option.ActiveCount} of {option.TotalCount} lines enabled."
            : $"{option.TotalCount} lines, all commented out.";

        return option.HasSuspectContent
            ? lines + $" {option.SuspectLines.Count} lines in this option look like they belong to something else."
            : lines;
    }

    /// <summary>
    /// Applies a switch from the tray.
    /// <para>
    /// Two things are deliberately not silent. A group holding quarantined lines is refused outright
    /// and sent to the tab, because agreeing to that needs the diff in front of you. And a dangerous
    /// option asks first — one stray click in a menu should not repoint a machine at production.
    /// </para>
    /// </summary>
    private async Task SwitchAsync(HostsGroup group, string? option)
    {
        if (_hosts.IsApplying) return;

        if (group.HasSuspectContent)
        {
            var review = MessageBox.Show(
                _uiOwner,
                $"'{group.Name}' claims lines that do not look like they belong to it, so switching it "
                + "here could comment out entries you rely on.\r\n\r\nOpen Host Changer to see exactly "
                + "which lines are affected?",
                "Review this change first",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (review == DialogResult.Yes) OpenTab();
            return;
        }

        var severity = option is null
            ? HostsSeverityLevel.Normal
            : group.Find(option)?.Severity ?? HostsSeverityLevel.Normal;

        if (severity == HostsSeverityLevel.Danger)
        {
            var confirm = MessageBox.Show(
                _uiOwner,
                $"Switch '{group.Name}' to '{option}'?\r\n\r\nThis option is flagged as dangerous.",
                "Confirm switch",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes) return;
        }

        try
        {
            var result = await _hosts.ApplyAsync(group.Name, option, new HostsApplyOptions());

            if (!result.Success && result.Status != HostsApplyStatus.ElevationDeclined)
            {
                MessageBox.Show(
                    _uiOwner,
                    result.Error ?? "The change could not be made.",
                    "Host Changer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"Tray switch failed: {ex.Message}");
        }
    }

    private void OpenTab()
    {
        _showWindow();
        _shell.RequestNavigation("/host-changer");
    }

    /// <summary>Wraps an async action so a menu click cannot throw into the message loop.</summary>
    private ToolStripMenuItem Item(string text, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);

        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Debug.WriteLine($"Tray action '{text}' failed: {ex.Message}");
            }
        };

        return item;
    }

    // ── the icons ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the three states rather than shipping image files.
    /// <para>
    /// Three colours of one simple mark is less to maintain than three binaries, it keeps the
    /// repository free of assets belonging to another team's app, and it cannot drift out of step
    /// with the severity levels it represents.
    /// </para>
    /// </summary>
    private Icon IconFor(HostsSeverityLevel level)
    {
        if (_icons.TryGetValue(level, out var existing)) return existing;

        var colour = level switch
        {
            HostsSeverityLevel.Danger => Color.FromArgb(239, 68, 68),
            HostsSeverityLevel.Caution => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(16, 185, 129),
        };

        var icon = Draw(colour);
        _icons[level] = icon;

        return icon;
    }

    /// <summary>A routing mark: one node on the left branching to two on the right.</summary>
    private static Icon Draw(Color colour)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(colour);
            g.FillEllipse(background, 0, 0, size - 1, size - 1);

            using var pen = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 9, 16, 17, 9);
            g.DrawLine(pen, 9, 16, 17, 23);

            using var node = new SolidBrush(Color.White);
            g.FillEllipse(node, 6, 13, 6, 6);
            g.FillEllipse(node, 15, 6, 6, 6);
            g.FillEllipse(node, 15, 20, 6, 6);
        }

        // GetHicon hands back a handle this process owns, so it is turned into an Icon that carries
        // its own copy and the handle is released immediately.
        var handle = bitmap.GetHicon();

        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hosts.Changed -= OnHostsChanged;
        _menu.Opening -= OnMenuOpening;

        _icon.Visible = false;

        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();

        _menu.Dispose();
    }
}
