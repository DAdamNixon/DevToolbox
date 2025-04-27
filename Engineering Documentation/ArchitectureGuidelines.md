# DevToolbox Architecture Guidelines

This document outlines the architectural patterns and guidelines for the DevToolbox application to ensure consistency and maintainability.

## Project Structure

The application follows a layered architecture with the following components:

1. **DevToolbox.UI**: User interface layer
   - Contains Blazor components, pages, and UI-specific models and services
   - Should only contain code directly related to the presentation layer

2. **DevToolbox.Services**: Business logic and services layer
   - Contains core business logic, domain services, and data access
   - Houses the application's primary functionality
   - Should not have dependencies on UI components

## Model Organization

Models should be organized according to their usage:

1. **Domain Models**: Located in `DevToolbox.Services/Models`
   - Core business entities (Workspace, WorkspaceGroup, etc.)
   - Persistence models
   - Domain-specific types and enums

2. **View Models**: Located in `DevToolbox.UI/Models`
   - UI-specific representations of domain models
   - Display-oriented properties and formatting
   - Should reference domain models but not vice versa

## Service Organization

Services should be organized by their responsibility:

1. **UI Services**: Located in `DevToolbox.UI/Services`
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

- All services should be registered in Program.cs
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