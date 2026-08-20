using System.Text.Json.Serialization;
using DevToolbox.Services.Models;

namespace DevToolbox.UI.Models
{
    /// <summary>
    /// UI-specific representation of a Workspace
    /// </summary>
    public class WorkspaceViewModel
    {
        /// <summary>
        /// The underlying domain model
        /// </summary>
        public Workspace Workspace { get; set; } = null!;

        /// <summary>
        /// Gets or sets a value indicating whether this workspace is expanded in the UI
        /// </summary>
        public bool IsExpanded { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this workspace is selected in the UI
        /// </summary>
        public bool IsSelected { get; set; }

        /// <summary>
        /// Identity used for expand state and modal keys.
        /// <para>
        /// Deliberately not the workspace id: ids in workspaceGroups.yaml are only unique
        /// within a group, so several cards share one. Keying on it made expanding one card
        /// expand its id-twins in other groups and made their dialogs open together.
        /// Group + name is what actually identifies a card, and it survives a rescan.
        /// </para>
        /// </summary>
        public string StateKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets a shortcut to the workspace name
        /// </summary>
        public string Name => Workspace.Name;

        /// <summary>
        /// Gets a shortcut to the workspace group name
        /// </summary>
        public string GroupName => Workspace.GroupName;

        /// <summary>
        /// Gets a shortcut to the workspace locations
        /// </summary>
        public List<WorkspaceLocation> Locations => Workspace.Locations;
    }

    /// <summary>
    /// UI-specific representation of a WorkspaceGroup
    /// </summary>
    public class WorkspaceGroupViewModel
    {
        /// <summary>
        /// The underlying domain model
        /// </summary>
        public WorkspaceGroup Group { get; set; } = null!;
        
        /// <summary>
        /// Gets or sets the UI-specific workspaces contained in this group
        /// </summary>
        public List<WorkspaceViewModel> Workspaces { get; set; } = new();
        
        /// <summary>
        /// Gets or sets a value indicating whether this group is expanded in the UI
        /// </summary>
        public bool IsExpanded { get; set; }

        /// <summary>
        /// Gets the ID string used for CardStateService
        /// </summary>
        public string StateKey => $"group_{Group.Name}";

        /// <summary>
        /// Gets a shortcut to the group name
        /// </summary>
        public string Name => Group.Name;

        /// <summary>
        /// Gets a shortcut to the group ID
        /// </summary>
        public int Id => Group.Id;
    }

    /// <summary>
    /// UI-specific representation of a CustomOpenOption
    /// </summary>
    public class CustomOpenOptionViewModel
    {
        /// <summary>
        /// Gets or sets the name of the option
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the display name of the option
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the icon class for the option
        /// </summary>
        public string? IconClass { get; set; }
        
        /// <summary>
        /// Gets or sets the underlying domain model
        /// </summary>
        public CustomOpenOption Option { get; set; } = null!;
    }
} 