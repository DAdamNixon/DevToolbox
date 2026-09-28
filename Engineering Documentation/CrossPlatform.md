# DevToolbox on Windows, Linux and macOS

DevToolbox has one UI and one set of services for every platform. What differs by operating system sits behind an interface, with one implementation for Windows and one for Linux and macOS. The Windows app registers the same classes it always has, so it behaves exactly as before.

## Projects

| Project | Target | What it is |
|---|---|---|
| `DevToolbox.Services` | `net10.0` | The platform-neutral core: workspaces, config, Host Changer's parsing, Service Pulse, PowerShell, and the interfaces for everything OS-specific. |
| `DevToolbox.Logs` | `net10.0` | The Log Viewer's data layer. |
| `DevToolbox.Services.Windows` | `net10.0`, Windows-only | `SystemService` (Explorer, Windows Terminal, cmd, App Paths), the UAC hosts writer, the ACL grant. Moved here unchanged from `DevToolbox.Services`. |
| `DevToolbox.Services.Unix` | `net10.0`, not Windows | The same interfaces for Linux and macOS: `xdg-open` / `open`, zenity dialogs, the hosts writer through `pkexec`. |
| `DevToolbox.UI.Shared` | `net10.0` Razor class library | Every page, component, stylesheet, script and theme. |
| `DevToolbox.UI` | `net10.0-windows` | The Windows host: Windows Forms window, WebView, tray icon, single instance. **Still the project the installer publishes.** |
| `DevToolbox.UI.Linux` | `net10.0` | The Linux host (assembly `devtoolbox`): a native window (PhotinoX, WebKitGTK) with the browser view beside it, a tray icon, single instance. Chrome app window as the fallback. |
| `DevToolbox.DevServer` | `net10.0` | The browser-only dev tool; picks Windows or Unix services at startup. |
| `DevToolbox.Mcp` | `net10.0` | The MCP server. Still `win-x64` when built on Windows. |
| `DevToolbox.Cli` | `net10.0` | The command line (`devtoolbox-cli`, and `devtoolbox projects …` / `devtoolbox logs …` on Linux). Log search runs on the MCP server's `LogViewerService`. |
| `DevToolbox.Tests` | `net10.0` | The suite. Runs on Linux too; Windows-only tests use `[WindowsFact]` and skip elsewhere. |
| `DevToolbox.Tests.Windows` | `net10.0-windows` | The tests that need a real Windows Forms window. |

## Rules for changing the UI

- **Pages and components go in `DevToolbox.UI.Shared`.** A change there reaches every platform with nothing else to do.
- **No `OperatingSystem.Is…()` checks and no `System.Windows.Forms` in a `.razor` file.** If a page needs something the platforms do differently, it goes through a service interface. See the table below.
- **Static assets keep their root URLs.** The library serves `wwwroot/` at `/` (`StaticWebAssetBasePath`), so `css/theme.css` is still `css/theme.css`.
- **A new stylesheet or script** goes into all three host pages: `DevToolbox.UI/wwwroot/index.html` (the Windows WebView), `DevToolbox.UI.Linux/wwwroot/index.html` (the Linux window, a copy of the Windows one) and `DevToolbox.UI.Shared/Web/Root.razor` (every browser surface).
- **CSS has to work in WebKit too.** The Linux window is WebKitGTK, the engine family Safari uses, and the macOS one would be WKWebView. Every tab and theme was checked there without a change; if something draws differently, fix it in the shared CSS, never with a per-platform page.

## The platform seams

| Interface | Windows | Linux / macOS | Registered by |
|---|---|---|---|
| `ISystemService` | `SystemService` | `UnixSystemService` | `AddWindowsPlatform()` / `AddUnixPlatform()` |
| `IHostsWriteBroker` | `HostsWriteBroker` (UAC, `runas`) | `UnixHostsWriteBroker` (`pkexec`) | same |
| `IHostsPermissionService` | `HostsPermissionService` (ACL) | `UnixHostsPermissionService` (`setfacl`) | same |
| `IPathPicker` | `WinFormsPathPicker` (in `DevToolbox.UI`) | `UnixPathPicker` (zenity / AppleScript) | the host |

`ServiceRegistration.AddDevToolboxApp()` registers everything else. The container is built with `ValidateOnBuild`, so a host that forgets a platform service fails at startup with the missing type's name.

A few model defaults are OS-specific and decide it themselves: the system hosts file path (`HostsSettings.DefaultHostsPath`) and the starter DNS flush (`HostsSettings.CreateStarter`).

## The Linux host

Shaped like the Windows host, part for part:

| | Windows (`DevToolbox.UI`) | Linux (`DevToolbox.UI.Linux`) |
|---|---|---|
| Window | Windows Forms + WebView2 | PhotinoX (`PhotinoWindow`): GTK + WebKitGTK 4.1 |
| Owns the singletons | the Windows Forms container | the PhotinoX container |
| Browser view at 5218 | `WebPreviewHost`, borrowing them | the same |
| Tray | `HostsTrayIcon` (`NotifyIcon`) | `AppIndicatorTray` (libayatana-appindicator), behind `ITrayIcon` |
| Single instance | named mutex + a broadcast window message | `instance.lock` + a Unix socket, `$XDG_RUNTIME_DIR/devtoolbox.sock` |
| Notifications | tray balloons | `notify-send` |

- `devtoolbox` opens the window. Launching it again brings the running window forward: the new process passes its launcher's activation token over the socket, which is what lets a window take focus on Wayland. From a terminal there is no token, and GNOME shows "DevToolbox is ready" instead.
- `devtoolbox --browser`, or `openIn: browser` in `Config/linux.yaml`, uses a Chrome, Chromium, Edge or Brave app window instead (or the default browser). So does a machine with no display or no WebKitGTK, which says why on stderr.
- `devtoolbox --no-window` (the login autostart) starts with the window hidden and only the tray icon showing. `devtoolbox --quit` stops the running copy, over the socket or with `SIGTERM`, and waits until it has exited.
- The tray has the Windows menu (hosts switches, Open HOSTS file and folder, Open Host Changer, Show, Exit) plus **Open in browser**. Each group's state is in its label, since GNOME shows no tooltip. GNOME only shows tray icons with the AppIndicator extension (`ubuntu-appindicators@ubuntu.com` on Ubuntu, where it may be disabled).
- Closing the window hides it to the tray when Host Changer's "minimize to tray" is on and a panel is showing the icon, with the same one-time hint (`TrayHintShown`). Otherwise closing quits.
- `DEVTOOLBOX_DEVTOOLS=1` turns on the WebKit inspector (right-click → Inspect), prints the page's console in the terminal, and lets PhotinoX log what it does.
- PhotinoX serves only a real `wwwroot` folder, and a build has none for the shared library's files, so `WebRoot` lays the static web assets manifest over it with ASP.NET's `StaticWebAssetsLoader`. A publish has the folder and no manifest.
- The PhotinoX packages are pinned to an exact version. Their native library runs inside the app, so read what changed in a new release before moving to it.
- `DevToolbox.UI.Linux/packaging/install.sh` installs it for the current user, with no root: the app under `~/.local/share/DevToolbox/bin/app`, a launcher in the app menu, and `~/.local/bin/devtoolbox`.
- Starting it from VS Code's terminal inherits `GDK_BACKEND=x11`, so the window runs under XWayland. Use `env -u GDK_BACKEND devtoolbox`, or the app menu, to see what users get.
- Config is in `~/.local/share/DevToolbox/Config` (what `LocalApplicationData` is on Linux). A Linux `openHandlers.yaml` in `DevToolbox.UI.Linux/ConfigDefaults` replaces the Windows one in that host's output.
- Host Changer writes `/etc/hosts` through `pkexec`, which shows the desktop's password prompt. The step that runs as root is `UnixHostsWriteBroker.ElevatedScript`. It only checks two hashes and swaps a staged file in.

## The command line

The same projects and logs as the app, from a terminal. On Linux it is part of the `devtoolbox` command; elsewhere, run `devtoolbox-cli`.

```sh
devtoolbox projects swdr                       # search, abbreviations and aliases included
devtoolbox projects open DevToolbox            # what the card's Open button does
devtoolbox projects open DevToolbox --in terminal
devtoolbox logs locations
devtoolbox logs files -l "Sample logs"
devtoolbox logs search app -l "Sample logs" --from 2026-09-20 --to 2026-09-25 --terms Timeout
devtoolbox logs search app -l "Sample logs" --sql "SELECT Level, COUNT(*) FROM {table} GROUP BY Level"
```

Every command takes `--json`. Log search uses the MCP server's `LogViewerService`, so it has the same guardrails: only the locations named, file names that cannot leave a location, read-only queries, 200 rows a page, and a time limit. Each run has its own scratch database, deleted when it exits, so it never touches the app's `logs.db` and is safe beside the running app.

## Checking Windows after a change here

The Windows code cannot be run on Linux, but it can be built and packaged there:

```sh
dotnet build DevToolbox.sln -p:EnableWindowsTargeting=true
dotnet publish DevToolbox.UI -c Release -p:EnableWindowsTargeting=true -o /tmp/pub
```

Before a release, run this on a Windows machine:

1. The window opens, with the app icon in the title bar and taskbar.
2. The tray icon appears (if Host Changer's tray setting is on), and closing the window hides it to the tray.
3. Projects: Smart Folders → Browse opens the Windows folder dialog; picking a folder fills the path.
4. Settings → Default workspace location → Browse, and the Scripts tab's parameter Browse buttons, open the Windows dialogs.
5. A project card's menu says **Open in Explorer**, and it opens Explorer; **Open in Terminal** opens Windows Terminal.
6. Log Viewer → CSV opens Explorer with the exported file selected.
7. Host Changer switches an option, with the UAC prompt if the account has no write access.
8. The browser view at `http://localhost:5218` loads and is interactive.
9. `devtoolbox-mcp.exe --probe` passes. Note the MCP build output folder moved from `net10.0-windows\win-x64` to `net10.0\win-x64`, which the installer script has to look in.
