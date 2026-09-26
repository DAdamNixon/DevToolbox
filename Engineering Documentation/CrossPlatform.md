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
| `DevToolbox.UI.Linux` | `net10.0` | The Linux host (assembly `devtoolbox`): Kestrel on loopback and a Chrome app window. |
| `DevToolbox.DevServer` | `net10.0` | The browser-only dev tool; picks Windows or Unix services at startup. |
| `DevToolbox.Mcp` | `net10.0` | The MCP server. Still `win-x64` when built on Windows. |
| `DevToolbox.Tests` | `net10.0` | The suite. Runs on Linux too; Windows-only tests use `[WindowsFact]` and skip elsewhere. |
| `DevToolbox.Tests.Windows` | `net10.0-windows` | The tests that need a real Windows Forms window. |

## Rules for changing the UI

- **Pages and components go in `DevToolbox.UI.Shared`.** A change there reaches every platform with nothing else to do.
- **No `OperatingSystem.Is…()` checks and no `System.Windows.Forms` in a `.razor` file.** If a page needs something the platforms do differently, it goes through a service interface. See the table below.
- **Static assets keep their root URLs.** The library serves `wwwroot/` at `/` (`StaticWebAssetBasePath`), so `css/theme.css` is still `css/theme.css`.
- **A new stylesheet or script** goes into both host pages: `DevToolbox.UI/wwwroot/index.html` (the WebView) and `DevToolbox.UI.Shared/Web/Root.razor` (every browser surface).

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

- `devtoolbox` starts the server and opens a window. A second `devtoolbox` opens the running copy's window instead of starting another.
- `devtoolbox --no-window` starts the server only. `devtoolbox --quit` stops it.
- Closing the window leaves DevToolbox running, as the tray does on Windows. Service Pulse alerts arrive through `notify-send`.
- The window is Chrome, Chromium, Edge or Brave in `--app` mode, and the default browser otherwise.
- `DevToolbox.UI.Linux/packaging/install.sh` installs it for the current user, with no root: the app under `~/.local/share/DevToolbox/bin/app`, a launcher in the app menu, and `~/.local/bin/devtoolbox`.
- Config is in `~/.local/share/DevToolbox/Config` (what `LocalApplicationData` is on Linux). A Linux `openHandlers.yaml` in `DevToolbox.UI.Linux/ConfigDefaults` replaces the Windows one in that host's output.
- Host Changer writes `/etc/hosts` through `pkexec`, which shows the desktop's password prompt. The step that runs as root is `UnixHostsWriteBroker.ElevatedScript`. It only checks two hashes and swaps a staged file in.

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
