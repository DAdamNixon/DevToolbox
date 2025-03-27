# DevToolbox

A comprehensive desktop application for development tools and utilities built with .NET, Blazor, and Windows Forms.

## Features

- **Workspace Management**: Organize and manage development workspaces
- **Log Viewer**: View and analyze application logs
- **Settings**: Configure application preferences
- **Modern UI**: Clean and responsive user interface built with Blazor

## Technical Details

- **Framework**: .NET 9.0 Windows
- **UI Technology**: Blazor WebView in Windows Forms
- **Display Scaling**: Properly handles high DPI displays with PerMonitorV2 DPI awareness

## Development

### Prerequisites

- .NET 9.0 SDK or later
- Visual Studio 2022 or later (recommended)

### Getting Started

1. Clone the repository
   ```
   git clone https://your-repository-url/DevToolbox.git
   ```

2. Open the solution in Visual Studio or build from command line
   ```
   dotnet build
   ```

3. Run the application
   ```
   dotnet run --project DevToolbox.UI
   ```

## Project Structure

- **DevToolbox.UI**: Main application UI components (Blazor WebView + Windows Forms)
- **DevToolbox.Core**: (Future) Core business logic and services

## License

[MIT License](LICENSE) 