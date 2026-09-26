# DevToolbox

A comprehensive desktop application for development tools and utilities built with .NET, Blazor, and Windows Forms.

## Features

- **Workspace Management**: Organize and manage development workspaces
- **Log Viewer**: View and analyze application logs
- **PowerShell Scripting**: Run and manage PowerShell scripts
- **Settings**: Configure application preferences
- **Modern UI**: Clean and responsive user interface built with Blazor

## Technical Details

- **Framework**: .NET 9.0 Windows
- **UI Technology**: Blazor WebView in Windows Forms
- **Display Scaling**: Properly handles high DPI displays with PerMonitorV2 DPI awareness
- **PowerShell Integration**: Microsoft.PowerShell.SDK for script execution

## Development

### Prerequisites

- .NET 9.0 SDK or later
- Visual Studio 2022 or later (recommended)
- VS Code (supported with included launch settings)

### Getting Started

1. Clone the repository
   ```
   git clone https://your-repository-url/DevToolbox.git
   ```

2. Open the solution in Visual Studio or build from command line
   ```
   dotnet build
   ```

3. Run the application (either way works)
   ```
   dotnet run --project DevToolbox.UI
   ```
   or simply
   ```
   dotnet run
   ```

4. VS Code users can press F5 to debug or use the included tasks:
   - `Ctrl+Shift+B` to run the application
   - Terminal > Run Task > build/run for other options

### Linux

DevToolbox also runs on Linux, in an app window over a local server. It needs the .NET 10 SDK (`sudo apt install dotnet-sdk-10.0`).

```
./DevToolbox.UI.Linux/packaging/install.sh
```

That installs it for your user only and adds it to the app menu. Run `devtoolbox` to open it, and `devtoolbox --quit` to stop it. The same command works from a terminal: `devtoolbox projects <search>`, `devtoolbox projects open <name>`, and `devtoolbox logs search …` (add `--help` to any of them). For a quick run from the repo, use `dotnet run --project DevToolbox.UI.Linux`. How the platforms fit together is in [Engineering Documentation/CrossPlatform.md](Engineering%20Documentation/CrossPlatform.md).

## Project Structure

- **DevToolbox.UI.Shared**: Every page, component, stylesheet and theme, for every platform
- **DevToolbox.UI**: The Windows app (Windows Forms + Blazor WebView)
- **DevToolbox.UI.Linux**: The Linux app
- **DevToolbox.Services**: The platform-neutral services, PowerShell and script management
- **DevToolbox.Services.Windows** / **DevToolbox.Services.Unix**: What each platform does differently
- **DevToolbox.Cli**: The command line: projects and log search
- **DevToolbox.Services/Scripts**: PowerShell script collection

## PowerShell Scripts

The application comes with several built-in PowerShell scripts:

- **CleanBuildArtifacts**: Removes bin, obj, node_modules folders and package-lock.json files from projects
- **GetSystemInfo**: Collects and displays detailed system information

## License

[MIT License](LICENSE) 