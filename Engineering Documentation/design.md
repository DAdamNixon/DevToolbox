# Dev Toolkit Design Document

## 1. Architecture Overview

### 1.1 Application Structure
```
DevToolkit/
├── src/
│   ├── DevToolkit.Core/           # Core business logic and interfaces
│   ├── DevToolkit.UI/             # Windows Forms Blazor UI components
│   ├── DevToolkit.Services/       # Implementation of core services
│   ├── DevToolkit.Plugins/        # Plugin system and base plugin classes
│   └── DevToolkit.Common/         # Shared utilities and models
├── tests/
│   ├── DevToolkit.Core.Tests/
│   ├── DevToolkit.Services.Tests/
│   └── DevToolkit.UI.Tests/
└── plugins/                        # Third-party plugins directory
```

### 1.2 Core Components
- **Main Application Shell**: Windows Forms host with Blazor WebAssembly integration
- **Docking System**: Custom docking implementation for panel management
- **Plugin Host**: Plugin loading and management system
- **Configuration Manager**: .HOST file parsing and management
- **Log Parser Engine**: Modular log parsing system with format-specific parsers
- **File System Monitor**: Windows-specific file system event monitoring
- **Workspace Manager**: Project and workspace state management

## 2. Implementation Details

### 2.1 Core Services

#### Log Parser Service
```csharp
public interface ILogParserService
{
    Task<LogEntry[]> ParseLogFileAsync(string filePath, LogParserOptions options);
    IObservable<LogEntry> StreamLogEntriesAsync(string filePath);
    Task<bool> ValidateLogFormatAsync(string filePath, LogFormat format);
}
```

#### File Monitor Service
```csharp
public interface IFileMonitorService
{
    void StartMonitoring(string path, FileMonitorOptions options);
    void StopMonitoring(string path);
    IObservable<FileSystemEventArgs> GetFileSystemEvents();
}
```

#### Workspace Service
```csharp
public interface IWorkspaceService
{
    Task<Workspace> LoadWorkspaceAsync(string path);
    Task SaveWorkspaceAsync(Workspace workspace);
    Task<Workspace[]> GetRecentWorkspacesAsync();
}
```

### 2.2 UI Components

#### Main Window Structure
```csharp
public class MainWindow : Form
{
    private BlazorWebView _mainView;
    private DockPanel _dockPanel;
    private StatusStrip _statusStrip;
    private MenuStrip _menuStrip;
}
```

#### Blazor Components
```razor
// MainLayout.razor
@inherits LayoutComponentBase

<div class="main-layout">
    <DockPanel @bind-Value="@LayoutState">
        <LogViewerPanel />
        <FileExplorerPanel />
        <WorkspacePanel />
    </DockPanel>
</div>
```

### 2.3 Plugin System
```csharp
public interface IDevToolkitPlugin
{
    string Name { get; }
    string Version { get; }
    Task InitializeAsync();
    Task ShutdownAsync();
}
```

## 3. Data Models

### 3.1 Core Models
```csharp
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}

public class Workspace
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public Dictionary<string, string> Environment { get; set; }
    public List<WorkspaceShortcut> Shortcuts { get; set; }
}
```

## 4. Configuration Management

### 4.1 .HOST File Parser
```csharp
public class HostConfiguration
{
    public string Version { get; set; }
    public Dictionary<string, WorkspaceConfig> Workspaces { get; set; }
    public FileMonitoringConfig FileMonitoring { get; set; }
    public LogParsingConfig LogParsing { get; set; }
    public UIConfig UI { get; set; }
}
```

## 5. Implementation Phases

### Phase 1: Core Infrastructure (2-3 weeks)
1. Set up project structure and dependencies
2. Implement basic Windows Forms Blazor integration
3. Create core service interfaces and basic implementations
4. Implement basic configuration system

### Phase 2: Log Parser (2-3 weeks)
1. Implement log file parsing service
2. Create log viewer component
3. Add filtering and search capabilities
4. Implement real-time log tailing

### Phase 3: Workspace & File Monitoring (2 weeks)
1. Implement workspace management
2. Create file system monitoring service
3. Add workspace UI components
4. Implement file change notifications

### Phase 4: Plugin System & Polish (2 weeks)
1. Implement plugin infrastructure
2. Create sample plugins
3. Add UI polish and themes
4. Performance optimization

## 6. Technical Considerations

### 6.1 Performance
- Use memory-mapped files for large log files
- Implement virtual scrolling for log viewer
- Use background workers for CPU-intensive tasks
- Implement caching for frequently accessed data

### 6.2 Security
- Implement secure storage for sensitive data
- Validate all file operations
- Sandbox plugin execution
- Encrypt sensitive configuration data

### 6.3 Error Handling
- Implement comprehensive logging
- Add error recovery mechanisms
- Provide user-friendly error messages
- Implement automatic crash reporting

## 7. Testing Strategy

### 7.1 Unit Tests
- Core service implementations
- Configuration parsing
- Log parsing logic
- Plugin system

### 7.2 Integration Tests
- Service interactions
- File system operations
- Plugin loading and execution
- Configuration management

### 7.3 UI Tests
- Component rendering
- User interactions
- Layout management
- Theme switching

## 8. Deployment

### 8.1 Build Process
- Automated build pipeline
- Version management
- Dependency management
- Plugin packaging

### 8.2 Installation
- Windows installer package
- Automatic updates
- Plugin marketplace
- Configuration migration

## 9. Future Considerations

### 9.1 Potential Extensions
- Cloud logging integration
- Database query interface
- Container management
- Performance profiling

### 9.2 Scalability
- Distributed log processing
- Multiple workspace support
- Plugin ecosystem growth
- Configuration sharing 