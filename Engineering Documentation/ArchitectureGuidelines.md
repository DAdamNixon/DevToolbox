# DevToolbox Architecture Guidelines

This document outlines the architectural patterns and guidelines for the DevToolbox application to ensure consistency and maintainability.

## Project Structure

The application follows a layered architecture with the following components. How the platforms fit together is in [CrossPlatform.md](CrossPlatform.md).

1. **DevToolbox.UI.Shared**: User interface layer, for every platform
   - Contains Blazor components, pages, stylesheets, scripts and themes, and UI-specific models and services
   - Should only contain code directly related to the presentation layer
   - Never calls the operating system directly; platform differences come through service interfaces

2. **DevToolbox.Services**: Business logic and services layer
   - Contains core business logic, domain services, and data access
   - Houses the application's primary functionality
   - Should not have dependencies on UI components
   - Platform-neutral (`net10.0`); anything OS-specific is an interface here

3. **DevToolbox.Services.Windows** and **DevToolbox.Services.Unix**: the platform implementations of those interfaces

4. **Hosts**: `DevToolbox.UI` (the Windows window), `DevToolbox.UI.Linux`, and `DevToolbox.DevServer` (a browser-only dev tool)
   - Open a window or a browser and register their platform's services, and nothing else

5. **DevToolbox.Logs** and **DevToolbox.Mcp**: the Log Viewer's data layer, and the MCP server that exposes it to AI agents

## Model Organization

Models should be organized according to their usage:

1. **Domain Models**: Located in `DevToolbox.Services/Models`
   - Core business entities (Workspace, WorkspaceGroup, etc.)
   - Persistence models
   - Domain-specific types and enums

2. **View Models**: Located in `DevToolbox.UI.Shared/Models`
   - UI-specific representations of domain models
   - Display-oriented properties and formatting
   - Should reference domain models but not vice versa

## Service Organization

Services should be organized by their responsibility:

1. **UI Services**: Located in `DevToolbox.UI.Shared/Services`
   - Services that manage UI state (CardStateService)
   - UI-specific utilities and helpers
   - Should not contain business logic

2. **Business Services**: Located in `DevToolbox.Services/Services`
   - Core application functionality (WorkspaceService, PowerShellService)
   - Domain logic
   - Data processing

3. **Infrastructure Services**: Located in `DevToolbox.Services/Services`
   - External system integrations (YamlStorageService)
   - File system access, networking, etc.

## Dependency Injection

- Shared services are registered in `DevToolbox.UI.Shared/ServiceRegistration.cs`; platform services by each host (`AddWindowsPlatform`, `AddUnixPlatform`)
- Use interfaces for services to facilitate testing and maintain loose coupling
- Consider using scoped lifetime for services that maintain state during a user session

## Data Persistence

- Persistent data is stored in the user's AppData directory
- YamlStorageService manages serialization and deserialization of data
- Storage operations should be abstracted behind interfaces

## Coding Standards

1. Use consistent naming conventions:
   - PascalCase for class and property names
   - camelCase for private fields
   - Use descriptive names that express intent

2. Maintain separation of concerns:
   - UI components should not directly access data storage
   - Services should be focused on a single responsibility
   - Use interfaces to define contracts between layers

3. Error handling:
   - UI should provide user-friendly error messages
   - Services should log detailed errors
   - Use async/await consistently for asynchronous operations

## Future Considerations

1. Consider extracting a dedicated data access layer if the application grows
2. Implement unit and integration testing
3. Consider using a more structured architecture pattern like MVVM or Clean Architecture for larger modules 