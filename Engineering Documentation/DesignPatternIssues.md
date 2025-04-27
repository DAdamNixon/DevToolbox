# Design Pattern Inconsistencies

This document tracks design pattern issues and inconsistencies identified in the DevToolbox application that need to be addressed for better maintainability and code quality.

## UI Component Issues

1. **Modal Implementation Inconsistency**
   - **Issue**: In WorkspaceCard.razor, the modern `Modal` component with `@bind-IsVisible` pattern is used, while WorkspaceGroupCard.razor still uses the older Bootstrap modal approach with IDs and manually showing/hiding modals via JS.
   - **Fix**: Standardize on the modern `Modal` component approach across all components.

2. **Directory Structure Discrepancy**
   - **Issue**: Some modals are in a separate Dialogs directory while other modal content is embedded directly in component files.
   - **Fix**: Establish a consistent pattern - either extract all modal content to dedicated components in the Dialogs directory or keep simpler modals inline.

3. **ViewModels Usage Inconsistency**
   - **Issue**: Code mixes direct access to domain models and ViewModel wrappers in components like WorkspaceCard.razor (e.g., `WorkspaceViewModel.Workspace.Locations.Add(location)` directly modifies the domain model).
   - **Fix**: Consistently use ViewModels as intermediaries and avoid direct domain model modification from UI components.

4. **Modal State Management Inconsistency**
   - **Issue**: WorkspaceCard.razor uses local boolean variables to track modal visibility, and error messages are managed through a separate variable.
   - **Fix**: Create a consistent pattern for modal state management, possibly with a dedicated modal state service or standardized approach.

5. **Code Redundancy**
   - **Issue**: Error handling logic for checking existing workspace names is duplicated in both HandleMoveToGroup and HandleRename methods.
   - **Fix**: Extract duplicate validation logic to reusable validation methods or services.

6. **Process Handling in UI Component**
   - **Issue**: The OpenLocation method in WorkspaceCard.razor directly creates a Process and starts it.
   - **Fix**: Move system operations like process creation to appropriate services instead of handling them directly in UI components.

7. **Form Handling Inconsistency**
   - **Issue**: Some forms use extracted form components (e.g., AddWorkspaceLocation) while others use inline form elements.
   - **Fix**: Standardize on a consistent approach for form implementation.

## Implementation Plan

### Phase 1: Standardize Modal Implementation (High Priority)

1. **Create Missing Dialog Components**
   - Extract modal content from WorkspaceGroupCard.razor to dedicated components in `DevToolbox.UI/Components/Dialogs`
   - Create components such as `RenameWorkspaceDialog.razor`, `MoveWorkspaceDialog.razor`, etc.

2. **Convert Bootstrap Modals to Component-Based Modals**
   - Update WorkspaceGroupCard.razor to use the Modal component pattern with `@bind-IsVisible`
   - Remove JS-based modal code and IDs

3. **Standardize Modal State Management**
   - Create a `ModalStateService.cs` in DevToolbox.UI/Services to centrally manage modal visibility
   - Add support for error message management within this service

### Phase 2: Refactor System Services (Medium Priority)

1. **Create System Service**
   - Implement `ISystemService` interface with methods like `OpenLocation`, `OpenInExplorer`, etc.
   - Create concrete implementation `SystemService.cs` to handle all system operations 

2. **Refactor UI Components**
   - Update WorkspaceCard.razor to use the SystemService for operations instead of directly using Process

3. **Update Dependency Injection**
   - Register the new service in Program.cs
   - Inject the service into components that need system operations

### Phase 3: Improve ViewModels (Medium Priority)

1. **Enhance ViewModelFactory**
   - Add methods for modifying domain models through view models
   - Implement methods like `AddLocation`, `RenameWorkspace`, etc.

2. **Update Component Usage**
   - Modify WorkspaceCard.razor to use ViewModel methods instead of directly modifying domain models
   - Ensure all components follow the same pattern

### Phase 4: Extract Common Logic (Low Priority)

1. **Create Validation Service**
   - Implement `IValidationService` with methods for common validations
   - Add methods for name existence checks, path validations, etc.

2. **Update Handler Methods**
   - Replace duplicate validation code with calls to the validation service
   - Update HandleMoveToGroup and HandleRename in WorkspaceCard.razor

### Phase 5: Standardize Form Handling (Low Priority)

1. **Form Component Strategy**
   - Decide whether to use inline forms or extracted components based on complexity
   - Document the decision in ArchitectureGuidelines.md

2. **Refactor Forms**
   - Extract complex inline forms to components
   - Keep simple forms inline for better readability

## Effort Estimation

| Phase | Estimated Effort | Impact | Priority |
|-------|------------------|--------|----------|
| Phase 1 | 3 days | High | High |
| Phase 2 | 2 days | Medium | Medium |
| Phase 3 | 2 days | Medium | Medium |
| Phase 4 | 1 day | Low | Low |
| Phase 5 | 2 days | Low | Low |

## Success Criteria

- All components follow the same modal implementation pattern
- System operations are handled by appropriate services
- ViewModels are used consistently as intermediaries
- Validation logic is centralized and reused
- Form implementation follows consistent patterns
- Improved code maintainability and reduced duplication 