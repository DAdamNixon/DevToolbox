# Code Reorganization Tasks

This document outlines the structural and semantic issues identified in the DevToolbox application and the tasks to address them.

## Identified Issues

1. **Misplaced UI Services**: There's a `Services` folder in the UI project that's empty while UI-related services like `CardStateService` are placed in the `DevToolbox.Services` project.

2. **Unused Storage Folder**: There's a `Storage` folder at the root level that's empty and not part of the solution structure.

3. **Inconsistent Service Organization**: `PowerShellService.cs` is directly in the root of `DevToolbox.Services` while other services are in the `Services` subfolder.

4. **Inconsistent Model Organization**: The UI project has minimal models whereas more complex models are in the Services project.

5. **Data Persistence Structure**: There's no dedicated structure for managing storage operations beyond `YamlStorageService`.

6. **Documentation Organization**: The `Engineering Documentation` folder at the root level separates documentation from code.

## Tasks

1. Move UI-specific services to the `DevToolbox.UI/Services` directory
2. Move `PowerShellService.cs` to the `DevToolbox.Services/Services` folder for consistency
3. Remove or repurpose the empty `Storage` folder
4. Reorganize models to maintain a clearer separation of concerns
5. Enhance documentation with architecture guidelines

## Progress

- [x] Task 1: Move UI-specific services 
   - Moved `CardStateService` to the UI project
   - Updated namespace and references
- [x] Task 2: Reorganize PowerShellService
   - Moved `PowerShellService.cs` to the Services subfolder
   - Updated namespace and references in Program.cs
- [x] Task 3: Address Storage folder
   - Removed the empty Storage folder
- [x] Task 4: Reorganize models
   - Created UI-specific view models in the UI project
   - Maintained core domain models in the Services project
   - Added proper references between projects
- [x] Task 5: Update documentation
   - Created comprehensive architecture guidelines
   - Documented folder structure and responsibilities
   - Established patterns for future development

## Summary

All identified issues have been addressed, resulting in a more semantically correct and maintainable codebase. The application now has:

1. Clear separation between UI and business logic
2. Consistent service organization
3. Proper model separation based on responsibilities
4. Documented architecture guidelines
5. Removed unused or redundant elements 